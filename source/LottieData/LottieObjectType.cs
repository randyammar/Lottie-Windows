// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.Toolkit.Uwp.UI.Lottie.LottieData
{
#if !WINDOWS_UWP
    public
#endif
    enum LottieObjectType
    {
        Ellipse,
        ImageLayer,
        LinearGradientFill,
        LinearGradientStroke,
        LottieComposition,
        Marker,
        MergePaths,
        NullLayer,
        Polystar,
        PreCompLayer,
        RadialGradientFill,
        RadialGradientStroke,
        Rectangle,
        Repeater,
        RoundedCorner,
        Shape,
        ShapeGroup,
        ShapeLayer,
        SolidColorFill,
        SolidColorStroke,
        SolidLayer,
        TextLayer,
        Transform,
        TrimPath,
    }
}