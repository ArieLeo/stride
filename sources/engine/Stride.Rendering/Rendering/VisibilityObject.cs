// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;

namespace Stride.Rendering
{
    /// <summary>
    /// A single unit of culling in a <see cref="VisibilityGroup"/>.
    /// One <see cref="VisibilityObject"/> maps to one or more <see cref="RenderObject"/>s that
    /// share the same world-space bounding box for frustum and filter tests (e.g. all meshes
    /// of one entity). The frustum test and any injected <see cref="IVisibilityFilter"/>s run
    /// once per <see cref="VisibilityObject"/>; visible children are then expanded into
    /// <see cref="RenderView.RenderObjects"/> individually.
    /// </summary>
    /// <remarks>
    /// In the default 1:1 case (one <see cref="VisibilityObject"/> per <see cref="RenderObject"/>)
    /// behaviour is identical to the previous design. The N:1 grouping API is available for future
    /// use via <see cref="VisibilityGroup.AddRenderObjectToGroup"/>.
    /// </remarks>
    public class VisibilityObject
    {
        /// <summary>Whether this object participates in culling and rendering.</summary>
        public bool Enabled = true;

        /// <summary>Render group bitmask used to match <see cref="RenderView.CullingMask"/>.</summary>
        public RenderGroup RenderGroup;

        /// <summary>
        /// World-space axis-aligned bounding box used for frustum and filter tests.
        /// For Dynamic objects this is synced from the child <see cref="RenderObject.BoundingBox"/>
        /// every frame. For Static objects it is written once at registration and never updated.
        /// For N:1 groups the creator is responsible for keeping this up to date.
        /// </summary>
        public BoundingBoxExt BoundingBox;

        /// <summary>
        /// Controls how often <see cref="BoundingBox"/> is refreshed from child render objects.
        /// Set via <see cref="VisibilityGroup.SetObjectMobility"/>.
        /// </summary>
        public ObjectMobility Mobility = ObjectMobility.Dynamic;

        // ── Internal book-keeping ────────────────────────────────────────────────────────────

        /// <summary>
        /// Union of all children's render-stage bitmasks.
        /// Used for a fast stage-match early-out before frustum testing.
        /// Recomputed whenever render stage assignments change.
        /// Size equals <c>VisibilityGroup.stageMaskMultiplier</c>.
        /// </summary>
        internal uint[] CachedStageMask;

        /// <summary>
        /// Start index into <see cref="VisibilityGroup.RenderObjects"/> for this object's children.
        /// For the 1:1 default case this equals <see cref="VisibilityIndex"/>.
        /// </summary>
        internal int RenderObjectsOffset;

        /// <summary>Number of consecutive children starting at <see cref="RenderObjectsOffset"/>.</summary>
        internal int RenderObjectsCount;

        /// <summary>Index of this object in <c>VisibilityGroup._visibilityObjects</c>.</summary>
        internal int VisibilityIndex = -1;
    }
}
