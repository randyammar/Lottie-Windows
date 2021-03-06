// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.Toolkit.Uwp.UI.Lottie.LottieData
{
    /// <summary>
    /// A value that may be animated.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
#if PUBLIC_LottieData
    public
#endif
    class Animatable<T> : IAnimatableValue<T>
        where T : IEquatable<T>
    {
        readonly KeyFrame<T>[] _keyFrames;

        /// <summary>
        /// Initializes a new instance of the <see cref="Animatable{T}"/> class with
        /// a non-animated value.
        /// </summary>
        public Animatable(T value, int? propertyIndex)
        {
            Debug.Assert(value != null, "Precondition");
            _keyFrames = Array.Empty<KeyFrame<T>>();
            InitialValue = value;
            PropertyIndex = propertyIndex;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Animatable{T}"/> class with
        /// the given key frames.
        /// </summary>
        public Animatable(IEnumerable<KeyFrame<T>> keyFrames, int? propertyIndex)
        {
            _keyFrames = keyFrames.ToArray();

            // There must be a least one key frame otherwise this constructor should not have been called.
            InitialValue = _keyFrames[0].Value;

            if (_keyFrames.Length == 1)
            {
                // There's only one key frame so the value never changes. We have
                // saved the value in InitialValue. Might as well ditch the key frames.
                _keyFrames = Array.Empty<KeyFrame<T>>();
            }

            PropertyIndex = propertyIndex;

            Debug.Assert(_keyFrames.All(kf => kf != null), "Precondition");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Animatable{T}"/> class with
        /// the given key frames.
        /// </summary>
        public Animatable(T initialValue, in ReadOnlySpan<KeyFrame<T>> keyFrames, int? propertyIndex)
        {
            _keyFrames = keyFrames.Length > 1 ? keyFrames.ToArray() : Array.Empty<KeyFrame<T>>();
            InitialValue = initialValue;
            PropertyIndex = propertyIndex;

            Debug.Assert(initialValue != null, "Precondition");
            Debug.Assert(_keyFrames.All(kf => kf != null), "Precondition");
        }

        /// <summary>
        /// Gets the initial value.
        /// </summary>
        public T InitialValue { get; }

        /// <summary>
        /// Gets the keyframes that describe how the value should be animated.
        /// </summary>
        public ReadOnlySpan<KeyFrame<T>> KeyFrames => _keyFrames;

        /// <summary>
        /// Gets the property index used for expressions.
        /// </summary>
        public int? PropertyIndex { get; }

        /// <summary>
        /// Gets a value indicating whether the <see cref="Animatable{T}"/> has any key frames.
        /// </summary>
        public bool IsAnimated => _keyFrames.Length > 1;

        /// <summary>
        /// Returns <c>true</c> if this value is always equal to the given value.
        /// </summary>
        /// <returns><c>true</c> if this value is always equal to the given value.</returns>
        public bool AlwaysEquals(T value) => !IsAnimated && value.Equals(InitialValue);

        /// <inheritdoc/>
        // Not a great hash code because it ignore the KeyFrames, but quick.
        public override int GetHashCode() => InitialValue.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() =>
            IsAnimated
                ? string.Join(" -> ", _keyFrames.Select(kf => kf.Value.ToString()))
                : InitialValue.ToString();
    }
}
