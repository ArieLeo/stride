// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Rendering
{
    /// <summary>
    /// Extension point for custom per-view culling passes (e.g. occlusion culling, distance-based
    /// fading, gameplay visibility) that run after the built-in frustum test and before a
    /// <see cref="VisibilityObject"/> is expanded into <see cref="RenderView.RenderObjects"/>.
    /// </summary>
    /// <remarks>
    /// Thread-safety contract:
    /// <list type="bullet">
    ///   <item><see cref="PrepareView"/> is called single-threaded once per view, before the
    ///   parallel collection loop. Use it to build any per-view read-only structures (HZB tiles,
    ///   projected occluder sets, etc.).</item>
    ///   <item><see cref="IsVisible"/> is called concurrently from worker threads. It must only
    ///   read from data prepared in <see cref="PrepareView"/> (or other immutable frame data).
    ///   Do not write shared mutable state without synchronisation.</item>
    ///   <item><see cref="FinalizeView"/> is called single-threaded once per view after the
    ///   parallel loop. Use it to trigger readbacks, release per-view scratch buffers, or record
    ///   statistics.</item>
    /// </list>
    /// Filters are registered on <see cref="VisibilityGroup.Filters"/> and evaluated in list order.
    /// Any filter returning <c>false</c> rejects the object (short-circuit — remaining filters are
    /// skipped for that object). This is conservative: when in doubt, return <c>true</c>.
    /// </remarks>
    public interface IVisibilityFilter
    {
        /// <summary>
        /// Called once per view before the parallel collection loop.
        /// Build all read-only per-view data the filter will need during <see cref="IsVisible"/>.
        /// </summary>
        /// <param name="view">The view about to be collected.</param>
        void PrepareView(RenderView view);

        /// <summary>
        /// Returns <c>true</c> if the object should be included in the view's render object list,
        /// <c>false</c> to cull it. Called concurrently on worker threads.
        /// </summary>
        /// <param name="context">Read-only per-object context. The object has already passed frustum culling.</param>
        bool IsVisible(in VisibilityFilterContext context);

        /// <summary>
        /// Called once per view after the parallel collection loop.
        /// Release scratch allocations or trigger GPU readbacks for the next frame here.
        /// </summary>
        /// <param name="view">The view that was collected.</param>
        void FinalizeView(RenderView view);
    }
}
