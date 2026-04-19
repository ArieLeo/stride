// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;

namespace Stride.Rendering
{
    /// <summary>
    /// Immutable per-object context passed to <see cref="IVisibilityFilter.IsVisible"/> on worker threads.
    /// All data here is safe to read concurrently; do not write to any field.
    /// </summary>
    public readonly struct VisibilityFilterContext
    {
        /// <summary>The view being collected for.</summary>
        public readonly RenderView View;

        /// <summary>
        /// The visibility object being tested. Provides bounds, mobility, render group, and
        /// access to the children RenderObjects for the N:1 case.
        /// </summary>
        public readonly VisibilityObject VisibilityObject;

        /// <summary>
        /// World-space AABB of the visibility object. Already passed the frustum test when
        /// this context is constructed, so it overlaps the view frustum.
        /// </summary>
        public readonly BoundingBoxExt BoundingBox;

        public VisibilityFilterContext(RenderView view, VisibilityObject visibilityObject)
        {
            View = view;
            VisibilityObject = visibilityObject;
            BoundingBox = visibilityObject.BoundingBox;
        }
    }
}
