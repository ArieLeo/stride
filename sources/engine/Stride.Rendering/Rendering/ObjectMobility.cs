// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;

namespace Stride.Rendering
{
    /// <summary>
    /// Controls how frequently a render object's bounding box is updated for visibility culling.
    /// </summary>
    [DataContract]
    public enum ObjectMobility
    {
        /// <summary>
        /// Bounds are updated every frame. Use for anything that moves, animates, or changes shape.
        /// </summary>
        Dynamic = 0,

        /// <summary>
        /// Bounds are computed once at registration and never updated per-frame.
        /// Use for world geometry that never moves after scene load.
        /// </summary>
        Static = 1,
    }
}
