// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Threading;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Core.Diagnostics;

namespace Stride.Rendering
{
    /// <summary>
    /// Manages a set of registered <see cref="RenderObject"/>s grouped into
    /// <see cref="VisibilityObject"/>s for efficient culling.
    ///
    /// Per-frame pipeline (for each <see cref="RenderView"/>):
    /// <list type="number">
    ///   <item>Sync dynamic bounding boxes from child <see cref="RenderObject"/>s.</item>
    ///   <item>Run <see cref="IVisibilityFilter.PrepareView"/> on all registered filters.</item>
    ///   <item>Parallel loop over <see cref="VisibilityObject"/>s:
    ///     enabled/mask → stage-mask union → frustum → injected filters → expand children.</item>
    ///   <item>Run <see cref="IVisibilityFilter.FinalizeView"/> on all registered filters.</item>
    /// </list>
    /// </summary>
    public class VisibilityGroup : IDisposable
    {
        // ── Profiling ────────────────────────────────────────────────────────────────────────

        private static readonly ProfilingKey TryCollectKey        = new ProfilingKey("VisibilityGroup.Collect");
        private static readonly ProfilingKey SyncBoundsKey         = new ProfilingKey("VisibilityGroup.SyncDynamicBounds");
        private static readonly ProfilingKey RebuildMobilityKey    = new ProfilingKey("VisibilityGroup.RebuildMobilityLists");

        // ── Threading helpers ────────────────────────────────────────────────────────────────

        // Per-thread cache used when adding RenderObjects to a ConcurrentCollector.
        private readonly ThreadLocal<ConcurrentCollectorCache<RenderObject>> _collectorCache =
            new ThreadLocal<ConcurrentCollectorCache<RenderObject>>(() => new ConcurrentCollectorCache<RenderObject>(32));

        // ── Stage-mask book-keeping ──────────────────────────────────────────────────────────

        private int stageMaskMultiplier;

        public readonly StaticObjectPropertyKey<uint> RenderStageMaskKey;
        public const int RenderStageMaskSizePerEntry = 32;

        // ── Visibility objects ───────────────────────────────────────────────────────────────

        // Parallel to RenderObjects: _visibilityObjects[i] is the VO for RenderObjects[i] (1:1 default).
        private readonly List<VisibilityObject> _visibilityObjects = new List<VisibilityObject>();

        // Indices of Dynamic VOs inside _visibilityObjects; rebuilt every MobilityRebuildInterval frames.
        private readonly List<int> _dynamicVoIndices = new List<int>();
        private int _mobilityRebuildCounter;
        private const int MobilityRebuildInterval = 4;

        // ── Misc internal state ──────────────────────────────────────────────────────────────

        private readonly List<RenderObject> renderObjectsWithoutFeatures = new List<RenderObject>();

        internal bool NeedActiveRenderStageReevaluation;
        internal bool DisableCulling;

        // ── Public API ───────────────────────────────────────────────────────────────────────

        public RenderSystem RenderSystem { get; }

        /// <summary>Stores per-object render data (stage masks, etc.).</summary>
        public RenderDataHolder RenderData;

        /// <summary>Attached properties for this visibility group.</summary>
        public PropertyContainer Tags;

        /// <summary>All <see cref="RenderObject"/>s registered in this group.</summary>
        public RenderObjectCollection RenderObjects { get; }

        /// <summary>
        /// Optional culling filters that run after the built-in frustum test.
        /// Add/remove only outside of <see cref="TryCollect"/>; evaluated on worker threads
        /// read-only. See <see cref="IVisibilityFilter"/> for the thread-safety contract.
        /// </summary>
        public List<IVisibilityFilter> Filters { get; } = new List<IVisibilityFilter>();

        /// <summary>
        /// Returns the <see cref="VisibilityObject"/> at <paramref name="index"/>, which is
        /// parallel to <see cref="RenderObjects"/>[index] in the default 1:1 mapping.
        /// Used by external filters (e.g. GPU occlusion culling) to map a
        /// <see cref="RenderObject.VisibilityObjectNode"/> index back to its VO.
        /// Returns <c>null</c> if the index is out of range.
        /// </summary>
        public VisibilityObject GetVisibilityObject(int index) =>
            (uint)index < (uint)_visibilityObjects.Count ? _visibilityObjects[index] : null;

        // ── Construction / disposal ──────────────────────────────────────────────────────────

        public VisibilityGroup(RenderSystem renderSystem)
        {
            Tags = new PropertyContainer(this);
            RenderSystem = renderSystem;
            RenderObjects = new RenderObjectCollection(this);
            RenderData.Initialize(ComputeDataArrayExpectedSize);

            RenderStageMaskKey = RenderData.CreateStaticObjectKey<uint>(
                null,
                stageMaskMultiplier = (RenderSystem.RenderStages.Count + RenderStageMaskSizePerEntry - 1) / RenderStageMaskSizePerEntry);

            // Trigger mobility-list rebuild on first TryCollect.
            _mobilityRebuildCounter = MobilityRebuildInterval;

            RenderSystem.RenderStages.CollectionChanged        += RenderStages_CollectionChanged;
            RenderSystem.RenderStageSelectorsChanged           += RenderSystem_RenderStageSelectorsChanged;
            RenderSystem.RenderFeatures.CollectionChanged      += RenderFeatures_CollectionChanged;
        }

        public void Dispose()
        {
            RenderSystem.RenderStageSelectorsChanged      -= RenderSystem_RenderStageSelectorsChanged;
            RenderSystem.RenderStages.CollectionChanged   -= RenderStages_CollectionChanged;
        }

        public void Reset()
        {
            foreach (var renderObject in RenderObjects)
                renderObject.ObjectNode = ObjectNodeReference.Invalid;
        }

        // ── Collect ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Collects render objects visible in <paramref name="view"/> (skipped if already
        /// collected this frame).
        /// </summary>
        public void TryCollect(RenderView view)
        {
            using var _prof = Profiler.Begin(TryCollectKey);

            if (view.LastFrameCollected == RenderSystem.FrameCounter)
                return;

            view.LastFrameCollected = RenderSystem.FrameCounter;

            ReevaluateActiveRenderStages();

            // Rebuild dynamic/static index split periodically.
            if (++_mobilityRebuildCounter >= MobilityRebuildInterval)
            {
                _mobilityRebuildCounter = 0;
                RebuildMobilityLists();
            }

            // Sync Dynamic VO bounding boxes from their child RenderObject(s).
            SyncDynamicBounds();

            // View distance bounds.
            view.MinimumDistance = float.PositiveInfinity;
            view.MaximumDistance = float.NegativeInfinity;

            // Near-plane (for min/max distance calculation).
            Matrix.Invert(ref view.View, out var viewInverse);
            var planeNormal   = viewInverse.Forward;
            var pointOnPlane  = viewInverse.TranslationVector + viewInverse.Forward * view.NearClipPlane;
            var nearPlane     = new Plane(planeNormal, Vector3.Dot(pointOnPlane, planeNormal));

            // Build per-call view stage mask from ArrayPool (fixes the thread-safety bug on the
            // old shared instance field — each TryCollect call gets its own copy).
            var viewStageMask = ArrayPool<uint>.Shared.Rent(stageMaskMultiplier);
            try
            {
                Array.Clear(viewStageMask, 0, stageMaskMultiplier);
                foreach (var renderViewStage in view.RenderStages)
                {
                    var idx = renderViewStage.Index;
                    viewStageMask[idx / RenderStageMaskSizePerEntry] |= 1U << (idx % RenderStageMaskSizePerEntry);
                }

                var frustum     = new BoundingFrustum(ref view.ViewProjection);
                var cullingMode = DisableCulling ? CameraCullingMode.None : view.CullingMode;
                var cullingMask = view.CullingMask;

                // Single-threaded PrepareView on all registered filters.
                foreach (var filter in Filters)
                    filter.PrepareView(view);

                // Zero-alloc parallel collect over VisibilityObjects.
                var job = new VisibilityCollectJob
                {
                    Group              = this,
                    View               = view,
                    Frustum            = frustum,
                    CullingMode        = cullingMode,
                    CullingMask        = cullingMask,
                    ViewStageMask      = viewStageMask,
                    StageMaskMultiplier = stageMaskMultiplier,
                    NearPlane          = nearPlane,
                };

                Dispatcher.ForBatched(_visibilityObjects.Count, job);

                // Single-threaded FinalizeView on all registered filters.
                foreach (var filter in Filters)
                    filter.FinalizeView(view);

                view.RenderObjects.Close();
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(viewStageMask);
            }
        }

        /// <summary>
        /// Copies the already-collected visible set from <paramref name="source"/> into
        /// <paramref name="target"/> (used for VR shared-view culling).
        /// </summary>
        public void Copy(RenderView source, RenderView target)
        {
            target.LastFrameCollected = RenderSystem.FrameCounter;
            target.MinimumDistance    = source.MinimumDistance;
            target.MaximumDistance    = source.MaximumDistance;

            foreach (var renderObject in source.RenderObjects)
                target.RenderObjects.Add(renderObject);

            target.RenderObjects.Close();
        }

        // ── Mobility helpers ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets the <see cref="ObjectMobility"/> of the <see cref="VisibilityObject"/> that owns
        /// <paramref name="renderObject"/>. The mobility index lists are refreshed on the next
        /// rebuild interval.
        /// </summary>
        public void SetObjectMobility(RenderObject renderObject, ObjectMobility mobility)
        {
            if (renderObject.VisibilityObjectNode == StaticObjectNodeReference.Invalid)
                return;

            var voIndex = renderObject.VisibilityObjectNode.Index;
            if ((uint)voIndex < (uint)_visibilityObjects.Count)
                _visibilityObjects[voIndex].Mobility = mobility;
        }

        // ── Frustum utility (kept public — external code uses it, e.g. LightShafts) ─────────

        public static bool FrustumContainsBox(ref BoundingFrustum frustum, ref BoundingBoxExt boundingBoxExt, bool ignoreDepthPlanes)
        {
            unsafe
            {
                fixed (Plane* planeStart = &frustum.LeftPlane)
                {
                    var plane = planeStart;
                    for (int i = 0; i < 6; ++i)
                    {
                        if (ignoreDepthPlanes && i > 3)
                            continue;

                        if (Vector3.Dot(boundingBoxExt.Center, plane->Normal)
                            + boundingBoxExt.Extent.X * Math.Abs(plane->Normal.X)
                            + boundingBoxExt.Extent.Y * Math.Abs(plane->Normal.Y)
                            + boundingBoxExt.Extent.Z * Math.Abs(plane->Normal.Z)
                            <= -plane->D)
                            return false;

                        plane++;
                    }
                }
                return true;
            }
        }

        // ── Zero-alloc batch jobs ────────────────────────────────────────────────────────────

        /// <summary>
        /// Struct batch job for the main parallel visibility collect loop.
        /// Captured by value into <c>Dispatcher.ForBatched</c>; no heap allocation per frame.
        /// </summary>
        private struct VisibilityCollectJob : Dispatcher.IBatchJob
        {
            public VisibilityGroup     Group;
            public RenderView          View;
            public BoundingFrustum     Frustum;
            public CameraCullingMode   CullingMode;
            public RenderGroupMask     CullingMask;
            public uint[]              ViewStageMask;
            public int                 StageMaskMultiplier;
            public Plane               NearPlane;

            public void Process(int start, int endExclusive)
            {
                var cache   = Group._collectorCache.Value;
                int voCount = Group._visibilityObjects.Count;

                for (int i = start; i < endExclusive; i++)
                {
                    // Guard: if a structural change (unlikely but possible) shrank the list
                    // after ForBatched captured the count, skip rather than throw.
                    if ((uint)i >= (uint)voCount) break;

                    var vo = Group._visibilityObjects[i];

                    if (!vo.Enabled || ((RenderGroupMask)(1U << (int)vo.RenderGroup) & CullingMask) == 0)
                        continue;

                    // Stage-mask union early-out: skip if no child participates in any view stage.
                    if (!StageMaskMatchesView(vo))
                        continue;

                    // Frustum test (single AABB per VisibilityObject).
                    ref var bounds = ref vo.BoundingBox;
                    if (CullingMode == CameraCullingMode.Frustum
                        && bounds.Extent != Vector3.Zero
                        && !FrustumContainsBox(ref Frustum, ref bounds, View.VisiblityIgnoreDepthPlanes))
                        continue;

                    // Injected filters (short-circuit on first rejection).
                    if (!RunFilters(vo))
                        continue;

                    // Expand to individual RenderObjects.
                    Group.ExpandVisibilityObject(vo, View, ViewStageMask, StageMaskMultiplier, ref NearPlane, cache);
                }

                cache.Flush();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private unsafe bool StageMaskMatchesView(VisibilityObject vo)
            {
                var voMask = vo.CachedStageMask;
                if (voMask == null) return false;

                fixed (uint* viewPtr = ViewStageMask)
                fixed (uint* objPtr  = voMask)
                {
                    for (int j = 0; j < StageMaskMultiplier; j++)
                        if ((viewPtr[j] & objPtr[j]) != 0) return true;
                }
                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool RunFilters(VisibilityObject vo)
            {
                var filters = Group.Filters;
                if (filters.Count == 0) return true;

                var context = new VisibilityFilterContext(View, vo);
                for (int f = 0; f < filters.Count; f++)
                    if (!filters[f].IsVisible(in context)) return false;

                return true;
            }
        }

        /// <summary>
        /// Struct batch job for syncing Dynamic VO bounding boxes from their child RenderObjects.
        /// </summary>
        private struct SyncBoundsJob : Dispatcher.IBatchJob
        {
            public VisibilityGroup Group;

            public void Process(int start, int endExclusive)
            {
                int voCount = Group._visibilityObjects.Count;
                for (int i = start; i < endExclusive; i++)
                {
                    int voIdx = Group._dynamicVoIndices[i];

                    // Guard against stale indices from a structural change (add/remove) that
                    // happened between the last rebuild and this frame. The counter is reset on
                    // every structural change, so this path is only a safety net for races.
                    if ((uint)voIdx >= (uint)voCount)
                        continue;

                    var vo         = Group._visibilityObjects[voIdx];
                    int roOffset   = vo.RenderObjectsOffset;
                    int roCount    = Group.RenderObjects.Count;

                    // For the 1:1 default, sync the single child's bounds.
                    // For N:1 dynamic groups, the creator is responsible for keeping vo.BoundingBox current.
                    if (vo.RenderObjectsCount == 1 && (uint)roOffset < (uint)roCount)
                        vo.BoundingBox = Group.RenderObjects[roOffset].BoundingBox;
                }
            }
        }

        // ── Internal expand / collect helpers ────────────────────────────────────────────────

        /// <summary>
        /// Expands a visible <see cref="VisibilityObject"/> into its child <see cref="RenderObject"/>s,
        /// performing per-child stage-mask filtering before adding to the view's render list.
        /// Called from worker threads — all data accessed must be read-only for this frame.
        /// </summary>
        private void ExpandVisibilityObject(
            VisibilityObject vo,
            RenderView view,
            uint[] viewStageMask,
            int maskMultiplier,
            ref Plane nearPlane,
            ConcurrentCollectorCache<RenderObject> cache)
        {
            var renderStageMask = RenderData.GetData(RenderStageMaskKey);

            for (int i = 0; i < vo.RenderObjectsCount; i++)
            {
                var renderObject  = RenderObjects[vo.RenderObjectsOffset + i];
                var maskNode      = renderObject.VisibilityObjectNode * maskMultiplier;
                bool stageMatch   = false;

                unsafe
                {
                    fixed (uint* viewPtr = viewStageMask)
                    fixed (uint* objPtr  = renderStageMask.Data)
                    {
                        var v = viewPtr;
                        var o = objPtr + maskNode.Index;
                        for (int j = 0; j < maskMultiplier; j++)
                            if ((*v++ & *o++) != 0) { stageMatch = true; break; }
                    }
                }

                if (!stageMatch) continue;

                view.RenderObjects.Add(renderObject, cache);

                if (renderObject.BoundingBox.Extent != Vector3.Zero)
                    CalculateMinMaxDistance(ref nearPlane, ref renderObject.BoundingBox,
                        ref view.MinimumDistance, ref view.MaximumDistance);
            }
        }

        private void SyncDynamicBounds()
        {
            if (_dynamicVoIndices.Count == 0) return;
            using var _ = Profiler.Begin(SyncBoundsKey);
            Dispatcher.ForBatched(_dynamicVoIndices.Count, new SyncBoundsJob { Group = this });
        }

        private void RebuildMobilityLists()
        {
            using var _ = Profiler.Begin(RebuildMobilityKey);
            _dynamicVoIndices.Clear();
            for (int i = 0; i < _visibilityObjects.Count; i++)
                if (_visibilityObjects[i].Mobility == ObjectMobility.Dynamic)
                    _dynamicVoIndices.Add(i);
        }

        // ── RenderObject registration ────────────────────────────────────────────────────────

        internal void AddRenderObject(List<RenderObject> renderObjects, RenderObject renderObject)
        {
            if (renderObject.VisibilityObjectNode != StaticObjectNodeReference.Invalid)
                return;

            renderObject.VisibilityObjectNode = new StaticObjectNodeReference(renderObjects.Count);
            renderObjects.Add(renderObject);

            // Create a 1:1 VisibilityObject for this RenderObject (N:1 grouping is opt-in).
            var vo = new VisibilityObject
            {
                Enabled             = renderObject.Enabled,
                RenderGroup         = renderObject.RenderGroup,
                Mobility            = ObjectMobility.Dynamic,
                BoundingBox         = renderObject.BoundingBox,
                RenderObjectsOffset = renderObjects.Count - 1,
                RenderObjectsCount  = 1,
                VisibilityIndex     = _visibilityObjects.Count,
            };
            _visibilityObjects.Add(vo);

            // Immediately invalidate the dynamic-index list so SyncDynamicBounds is a no-op
            // this frame. The list is rebuilt at the start of the next TryCollect.
            _dynamicVoIndices.Clear();
            _mobilityRebuildCounter = MobilityRebuildInterval;

            RenderData.PrepareDataArrays();

            RenderSystem.AddRenderObject(renderObject);
            if (renderObject.RenderFeature != null)
                ReevaluateActiveRenderStages(renderObject);
            else
                renderObjectsWithoutFeatures.Add(renderObject);
        }

        internal bool RemoveRenderObject(List<RenderObject> renderObjects, [NotNull] RenderObject renderObject)
        {
            if (renderObject.RenderFeature == null)
                renderObjectsWithoutFeatures.Remove(renderObject);

            RenderSystem.RemoveRenderObject(renderObject);

            var orderedIndex = renderObject.VisibilityObjectNode.Index;
            if (renderObject.VisibilityObjectNode == StaticObjectNodeReference.Invalid)
                return false;

            renderObject.VisibilityObjectNode = StaticObjectNodeReference.Invalid;

            RenderData.SwapRemoveItem(DataType.StaticObject, orderedIndex, renderObjects.Count - 1);
            renderObjects.SwapRemoveAt(orderedIndex);

            // Parallel swap-remove on the VO list (maintains 1:1 invariant with renderObjects).
            _visibilityObjects.SwapRemoveAt(orderedIndex);

            // Immediately invalidate the dynamic-index list. Any index that pointed at the
            // removed VO (or the VO swapped into its slot) is now wrong. Clearing prevents
            // SyncDynamicBounds from using stale indices before the next rebuild.
            _dynamicVoIndices.Clear();
            _mobilityRebuildCounter = MobilityRebuildInterval;

            // Fix up indices of the item that was moved into the vacated slot.
            if (orderedIndex < renderObjects.Count)
            {
                renderObjects[orderedIndex].VisibilityObjectNode = new StaticObjectNodeReference(orderedIndex);

                var movedVo = _visibilityObjects[orderedIndex];
                movedVo.VisibilityIndex     = orderedIndex;
                movedVo.RenderObjectsOffset = orderedIndex;
            }

            return true;
        }

        // ── Stage-mask book-keeping ──────────────────────────────────────────────────────────

        /// <summary>
        /// Recomputes the cached stage-mask union for a single <see cref="VisibilityObject"/>
        /// from the current <see cref="RenderData"/> entries of its children.
        /// </summary>
        private void UpdateVoStageMask(VisibilityObject vo)
        {
            if (vo.CachedStageMask == null || vo.CachedStageMask.Length < stageMaskMultiplier)
                vo.CachedStageMask = new uint[stageMaskMultiplier];
            else
                Array.Clear(vo.CachedStageMask, 0, stageMaskMultiplier);

            var renderStageMask = RenderData.GetData(RenderStageMaskKey);
            for (int i = 0; i < vo.RenderObjectsCount; i++)
            {
                var ro       = RenderObjects[vo.RenderObjectsOffset + i];
                if (ro.VisibilityObjectNode == StaticObjectNodeReference.Invalid) continue;

                var maskNode = ro.VisibilityObjectNode * stageMaskMultiplier;
                for (int j = 0; j < stageMaskMultiplier; j++)
                    vo.CachedStageMask[j] |= renderStageMask[maskNode + j];
            }
        }

        private void ReevaluateActiveRenderStages(RenderObject renderObject)
        {
            var renderFeature = renderObject.RenderFeature;
            if (renderFeature == null) return;

            renderObject.ActiveRenderStages = new ActiveRenderStage[RenderSystem.RenderStages.Count];
            foreach (var selector in renderFeature.RenderStageSelectors)
                selector.Process(renderObject);

            var renderStageMask     = RenderData.GetData(RenderStageMaskKey);
            var renderStageMaskNode = renderObject.VisibilityObjectNode * stageMaskMultiplier;

            for (int index = 0; index < renderObject.ActiveRenderStages.Length; index++)
            {
                if (renderObject.ActiveRenderStages[index].Active)
                    renderStageMask[renderStageMaskNode + (index / RenderStageMaskSizePerEntry)]
                        |= 1U << (index % RenderStageMaskSizePerEntry);
            }

            // Update the owning VO's cached union mask.
            var voIdx = renderObject.VisibilityObjectNode.Index;
            if ((uint)voIdx < (uint)_visibilityObjects.Count)
                UpdateVoStageMask(_visibilityObjects[voIdx]);
        }

        private void ReevaluateActiveRenderStages()
        {
            if (!NeedActiveRenderStageReevaluation) return;
            NeedActiveRenderStageReevaluation = false;

            // Pass 1: recompute every RenderObject's stage mask in RenderData.
            var renderStageMask = RenderData.GetData(RenderStageMaskKey);
            foreach (var renderObject in RenderObjects)
            {
                var renderFeature = renderObject.RenderFeature;
                if (renderFeature == null) continue;

                renderObject.ActiveRenderStages = new ActiveRenderStage[RenderSystem.RenderStages.Count];
                foreach (var selector in renderFeature.RenderStageSelectors)
                    selector.Process(renderObject);

                var maskNode = renderObject.VisibilityObjectNode * stageMaskMultiplier;
                for (int i = 0; i < renderObject.ActiveRenderStages.Length; i++)
                    if (renderObject.ActiveRenderStages[i].Active)
                        renderStageMask[maskNode + (i / RenderStageMaskSizePerEntry)]
                            |= 1U << (i % RenderStageMaskSizePerEntry);
            }

            // Pass 2: recompute every VO's cached union from its children.
            foreach (var vo in _visibilityObjects)
                UpdateVoStageMask(vo);
        }

        // ── Distance calculation ─────────────────────────────────────────────────────────────

        private static void CalculateMinMaxDistance(
            ref Plane plane,
            ref BoundingBoxExt boundingBox,
            ref float minDistance,
            ref float maxDistance)
        {
            var nearCorner = boundingBox.Minimum;
            var farCorner  = boundingBox.Maximum;

            if (plane.Normal.X < 0) MemoryUtilities.Swap(ref nearCorner.X, ref farCorner.X);
            if (plane.Normal.Y < 0) MemoryUtilities.Swap(ref nearCorner.Y, ref farCorner.Y);
            if (plane.Normal.Z < 0) MemoryUtilities.Swap(ref nearCorner.Z, ref farCorner.Z);

            float oldDistance;
            var distance = CollisionHelper.DistancePlanePoint(ref plane, ref nearCorner);
            while ((oldDistance = minDistance) > distance
                   && Interlocked.CompareExchange(ref minDistance, distance, oldDistance) != oldDistance) { }

            distance = CollisionHelper.DistancePlanePoint(ref plane, ref farCorner);
            while ((oldDistance = maxDistance) < distance
                   && Interlocked.CompareExchange(ref maxDistance, distance, oldDistance) != oldDistance) { }
        }

        // ── DataArray size ───────────────────────────────────────────────────────────────────

        protected int ComputeDataArrayExpectedSize(DataType type)
        {
            switch (type)
            {
                case DataType.StaticObject: return RenderObjects.Count;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        // ── Event handlers ───────────────────────────────────────────────────────────────────

        private void RenderSystem_RenderStageSelectorsChanged()
        {
            // TODO GRAPHICS REFACTOR optimization: only reprocess objects with the changed RenderFeature.
            NeedActiveRenderStageReevaluation = true;
        }

        private void RenderFeatures_CollectionChanged(object sender, ref FastTrackingCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    for (int index = 0; index < renderObjectsWithoutFeatures.Count; index++)
                    {
                        var renderObject = renderObjectsWithoutFeatures[index];
                        if (renderObject.RenderFeature == null)
                        {
                            RenderSystem.AddRenderObject(renderObject);
                            if (renderObject.RenderFeature != null)
                            {
                                renderObjectsWithoutFeatures.SwapRemoveAt(index--);
                                ReevaluateActiveRenderStages(renderObject);
                            }
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Remove:
                    foreach (var renderObject in RenderObjects)
                    {
                        if (renderObject.RenderFeature == e.Item)
                        {
                            RenderSystem.RemoveRenderObject(renderObject);
                            renderObjectsWithoutFeatures.Add(renderObject);
                        }
                    }
                    break;
            }
        }

        private void RenderStages_CollectionChanged(object sender, ref FastTrackingCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                RenderData.ChangeDataMultiplier(
                    RenderStageMaskKey,
                    stageMaskMultiplier = (RenderSystem.RenderStages.Count + RenderStageMaskSizePerEntry - 1) / RenderStageMaskSizePerEntry);

                // viewRenderStageMask is now per-call (ArrayPool) so no resize needed here.
                // NeedActiveRenderStageReevaluation triggers UpdateVoStageMask for all VOs,
                // which also handles resizing vo.CachedStageMask arrays.
                NeedActiveRenderStageReevaluation = true;
            }
        }
    }
}
