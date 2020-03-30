// Copyright (c) Xenko contributors (https://xenko.com)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Xenko.Core;
using Xenko.Core.Annotations;
using Xenko.Core.Mathematics;
using Xenko.Core.Serialization.Contents;

namespace Xenko.Physics
{
    [ContentSerializer(typeof(DataContentSerializer<HeightfieldColliderShapeDesc>))]
    [DataContract("HeightfieldColliderShapeDesc")]
    [Display(300, "Heightfield")]
    public class HeightfieldColliderShapeDesc : IInlineColliderShapeDesc
    {
        [DataMember(10)]
        [NotNull]
        [Display(Expand = ExpandRule.Always)]
        public IHeightStickArraySource InitialHeights { get; set; } = new HeightStickArraySourceFromHeightmap();

        [DataMember(70)]
        public bool FlipQuadEdges = false;

        [DataMember(80)]
        [Display("Center height 0")]
        public bool CenterHeightZero = true;

        [DataMember(100)]
        public Vector3 LocalOffset;

        [DataMember(110)]
        public Quaternion LocalRotation = Quaternion.Identity;

        public bool Match(object obj)
        {
            var other = obj as HeightfieldColliderShapeDesc;

            if (other == null)
            {
                return false;
            }

            if (LocalOffset != other.LocalOffset || LocalRotation != other.LocalRotation)
            {
                return false;
            }

            var initialHeightsComparison = other.InitialHeights?.Match(InitialHeights) ?? InitialHeights == null;

            return initialHeightsComparison &&
                other.FlipQuadEdges == FlipQuadEdges &&
                other.CenterHeightZero == CenterHeightZero;
        }

        public static bool IsValidHeightStickSize(Int2 size)
        {
            return size.X >= HeightfieldColliderShape.MinimumHeightStickWidth && size.Y >= HeightfieldColliderShape.MinimumHeightStickLength;
        }

        private static void FillHeights<T>(UnmanagedArray<T> unmanagedArray, T value) where T : struct
        {
            if (unmanagedArray == null) throw new ArgumentNullException(nameof(unmanagedArray));

            for (int i = 0; i < unmanagedArray.Length; ++i)
            {
                unmanagedArray[i] = value;
            }
        }

        private static UnmanagedArray<T> CreateHeights<T>(int length, T[] initialHeights) where T : struct
        {
            var unmanagedArray = new UnmanagedArray<T>(length);

            if (initialHeights != null)
            {
                unmanagedArray.Write(initialHeights, 0, 0, Math.Min(unmanagedArray.Length, initialHeights.Length));
            }
            else
            {
                FillHeights(unmanagedArray, default);
            }

            return unmanagedArray;
        }

        public ColliderShape CreateShape()
        {
            if (InitialHeights == null ||
                !IsValidHeightStickSize(InitialHeights.HeightStickSize) ||
                InitialHeights.HeightRange.Y < InitialHeights.HeightRange.X ||
                Math.Abs(InitialHeights.HeightRange.Y - InitialHeights.HeightRange.X) < float.Epsilon ||
                Math.Abs(InitialHeights.HeightScale) < float.Epsilon)
            {
                return null;
            }

            var arrayLength = InitialHeights.HeightStickSize.X * InitialHeights.HeightStickSize.Y;

            object unmanagedArray;

            switch (InitialHeights.HeightType)
            {
                case HeightfieldTypes.Float:
                    {
                        unmanagedArray = CreateHeights(arrayLength, InitialHeights.Floats);
                        break;
                    }
                case HeightfieldTypes.Short:
                    {
                        unmanagedArray = CreateHeights(arrayLength, InitialHeights.Shorts);
                        break;
                    }
                case HeightfieldTypes.Byte:
                    {
                        unmanagedArray = CreateHeights(arrayLength, InitialHeights.Bytes);
                        break;
                    }

                default:
                    return null;
            }

            var offsetToCenterHeightZero = CenterHeightZero ? InitialHeights.HeightRange.X + ((InitialHeights.HeightRange.Y - InitialHeights.HeightRange.X) * 0.5f) : 0f;

            var shape = new HeightfieldColliderShape
                        (
                            InitialHeights.HeightStickSize.X,
                            InitialHeights.HeightStickSize.Y,
                            InitialHeights.HeightType,
                            unmanagedArray,
                            InitialHeights.HeightScale,
                            InitialHeights.HeightRange.X,
                            InitialHeights.HeightRange.Y,
                            FlipQuadEdges
                        )
                        {
                            LocalOffset = LocalOffset + new Vector3(0, offsetToCenterHeightZero, 0),
                            LocalRotation = LocalRotation,
                        };

            return shape;
        }
    }
}
