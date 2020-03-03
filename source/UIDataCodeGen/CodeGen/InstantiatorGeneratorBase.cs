// Copyright(c) Microsoft Corporation.All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.Toolkit.Uwp.UI.Lottie.GenericData;
using Microsoft.Toolkit.Uwp.UI.Lottie.UIData.Tools;
using Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData;
using Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData.MetaData;
using Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData.Mgcg;
using Expr = Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData.Expressions;
using Mgce = Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData.Mgce;
using Sn = System.Numerics;
using Wg = Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData.Wg;
using Wmd = Microsoft.Toolkit.Uwp.UI.Lottie.WinUIXamlMediaData;
using Wui = Microsoft.Toolkit.Uwp.UI.Lottie.WinCompData.Wui;

namespace Microsoft.Toolkit.Uwp.UI.Lottie.UIData.CodeGen
{
#if PUBLIC_UIDataCodeGen
    public
#endif
    abstract class InstantiatorGeneratorBase : IAnimatedVisualSourceInfo
    {
        // The name of the field holding the singleton reusable ExpressionAnimation.
        const string SingletonExpressionAnimationName = "_reusableExpressionAnimation";

        // The name of the field holding the theme properties.
        const string ThemePropertiesFieldName = "_themeProperties";

        // The name of the constant holding the duration of the animation in ticks.
        const string DurationTicksFieldName = "c_durationTicks";

        // The name of the IAnimatedVisualSource class.
        readonly string _className;
        readonly string _namespace;
        readonly Vector2 _compositionDeclaredSize;
        readonly TimeSpan _compositionDuration;
        readonly bool _setCommentProperties;
        readonly bool _disableFieldOptimization;
        readonly bool _generateDependencyObject;
        readonly Stringifier _stringifier;
        readonly IReadOnlyList<AnimatedVisualGenerator> _animatedVisualGenerators;
        readonly LoadedImageSurfaceInfo[] _loadedImageSurfaceInfos;
        readonly Dictionary<ObjectData, LoadedImageSurfaceInfo> _loadedImageSurfaceInfosByNode;
        readonly SourceMetadata _sourceMetadata;
        readonly bool _isThemed;
        readonly IReadOnlyList<string> _toolInfo;
        readonly string _interfaceType;

        AnimatedVisualGenerator _currentAnimatedVisualGenerator;

        protected InstantiatorGeneratorBase(
            CodegenConfiguration configuration,
            bool setCommentProperties,
            Stringifier stringifier)
        {
            _className = configuration.ClassName;
            _namespace = configuration.Namespace ?? "AnimatedVisuals";
            _compositionDeclaredSize = new Vector2((float)configuration.Width, (float)configuration.Height);
            _sourceMetadata = new SourceMetadata(configuration.SourceMetadata);
            _compositionDuration = configuration.Duration;
            _setCommentProperties = setCommentProperties;
            _disableFieldOptimization = configuration.DisableOptimization;
            _generateDependencyObject = configuration.GenerateDependencyObject;
            _stringifier = stringifier;
            _toolInfo = configuration.ToolInfo;
            _interfaceType = configuration.InterfaceType;
            var graphs = configuration.ObjectGraphs;

            _animatedVisualGenerators = graphs.Select(g => new AnimatedVisualGenerator(this, g.graphRoot, g.requiredUapVersion, graphs.Count > 1)).ToArray();

            // Determined whether theming is enabled.
            _isThemed = _animatedVisualGenerators.Any(avg => avg.IsThemed);

            // Deal with the nodes that are shared between multiple AnimatedVisual classes.
            // The nodes need naming, and some other adjustments.
            var sharedNodes = _animatedVisualGenerators.SelectMany(a => a.GetSharedNodes()).ToArray();

            // Canonicalize the loaded images surfaces.
            var sharedNodeGroups =
                (from n in sharedNodes
                 where n.IsLoadedImageSurface
                 let obj = (Wmd.LoadedImageSurface)n.Object
                 let key = obj.Type == Wmd.LoadedImageSurface.LoadedImageSurfaceType.FromUri
                             ? (object)((Wmd.LoadedImageSurfaceFromUri)obj).Uri
                             : ((Wmd.LoadedImageSurfaceFromStream)obj).Bytes
                 group n by key into g
                 select new SharedNodeGroup(g)).ToArray();

            // Generate names for each of the canonical nodes of the shared nodes (i.e. the first node in each group).
            foreach ((var n, var name) in NodeNamer<ObjectData>.GenerateNodeNames(sharedNodeGroups.Select(g => g.CanonicalNode)))
            {
                n.Name = name;
            }

            // Apply the name from the canonical node to the other nodes in its group so they will be
            // treated during generation as if they are the same object.
            foreach (var sharedNodeGroup in sharedNodeGroups)
            {
                var canonicalNode = sharedNodeGroup.CanonicalNode;
                if (canonicalNode.UsesAssetFile)
                {
                    // Set the Uri of the image file for LoadedImageSurfaceFromUri to $"ms-appx:///Assets/<className>/<filePath>/<fileName>.
                    var loadedImageSurfaceObj = (Wmd.LoadedImageSurfaceFromUri)canonicalNode.Object;
                    var imageUri = loadedImageSurfaceObj.Uri;

                    if (imageUri.IsFile)
                    {
                        canonicalNode.LoadedImageSurfaceImageUri = new Uri($"ms-appx:///Assets/{_className}{imageUri.AbsolutePath}");
                    }
                }

                // Propagate the name and Uri to the other nodes in the group.
                foreach (var n in sharedNodeGroup.Rest)
                {
                    n.Name = canonicalNode.Name;
                    n.LoadedImageSurfaceImageUri = canonicalNode.LoadedImageSurfaceImageUri;
                }
            }

            var sharedLoadedImageSurfaceInfos = (from g in sharedNodeGroups
                                                 where g.CanonicalNode.IsLoadedImageSurface
                                                 let loadedImageSurfaceNode = LoadedImageSurfaceInfoFromObjectData(g.CanonicalNode)
                                                 from node in OrderByName(g.All)
                                                 select (node, loadedImageSurfaceNode)).ToArray();

            _loadedImageSurfaceInfos = sharedLoadedImageSurfaceInfos.
                                            Select(n => n.loadedImageSurfaceNode).
                                            Distinct().
                                            OrderBy(lisi => lisi.Name, AlphanumericStringComparer.Instance).
                                            ToArray();

            _loadedImageSurfaceInfosByNode = sharedLoadedImageSurfaceInfos.ToDictionary(n => n.node, n => n.loadedImageSurfaceNode);
        }

        protected IAnimatedVisualSourceInfo AnimatedVisualSourceInfo => this;

        /// <summary>
        /// Takes a name and modifies it as necessary to be suited for use as a class name in languages such
        /// as  C# and C++.
        /// Returns null on failure.
        /// </summary>
        /// <returns>A name, or null.</returns>
        public static string TrySynthesizeClassName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Remove any non-characters from the start of the name.
            var nonCharPrefixSize = name.TakeWhile(c => !char.IsLetter(c)).Count();
            return SanitizeTypeName(name.Substring(nonCharPrefixSize));
        }

        /// <summary>
        /// Gets the standard header text used to indicate that a file contains auto-generated content.
        /// </summary>
        protected IReadOnlyList<string> AutoGeneratedHeaderText
        {
            get
            {
                var builder = new CodeBuilder();
                builder.WriteLine("//------------------------------------------------------------------------------");
                builder.WriteLine("// <auto-generated>");
                builder.WriteLine("//     This code was generated by a tool.");

                if (_toolInfo != null)
                {
                    builder.WriteLine("//");
                    foreach (var line in _toolInfo)
                    {
                        builder.WriteLine($"//       {line}");
                    }
                }

                builder.WriteLine("//");
                builder.WriteLine("//     Changes to this file may cause incorrect behavior and will be lost if");
                builder.WriteLine("//     the code is regenerated.");
                builder.WriteLine("// </auto-generated>");
                builder.WriteLine("//------------------------------------------------------------------------------");

                return builder.ToLines(0).ToArray();
            }
        }

        /// <summary>
        /// Writes the start of the file, e.g. using namespace statements and includes at the top of the file.
        /// </summary>
        protected abstract void WriteFileStart(CodeBuilder builder);

        /// <summary>
        /// Writes the start of the IAnimatedVisual implementation class.
        /// </summary>
        protected abstract void WriteAnimatedVisualStart(
            CodeBuilder builder,
            IAnimatedVisualInfo info);

        /// <summary>
        /// Writes the end of the IAnimatedVisual implementation class.
        /// </summary>
        protected abstract void WriteAnimatedVisualEnd(
            CodeBuilder builder,
            IAnimatedVisualInfo info);

        /// <summary>
        /// Writes the end of the file.
        /// </summary>
        protected abstract void WriteFileEnd(CodeBuilder builder);

        /// <summary>
        /// Writes CanvasGeometery.Combination factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="obj">Describes the object that should be instantiated by the factory code.</param>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryCombinationFactory(
            CodeBuilder builder,
            CanvasGeometry.Combination obj,
            string typeName,
            string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.Ellipse factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="obj">Describes the object that should be instantiated by the factory code.</param>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryEllipseFactory(
            CodeBuilder builder,
            CanvasGeometry.Ellipse obj,
            string typeName,
            string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.Ellipse factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="obj">Describes the object that should be instantiated by the factory code.</param>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryGroupFactory(
            CodeBuilder builder,
            CanvasGeometry.Group obj,
            string typeName,
            string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.Path factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="obj">Describes the object that should be instantiated by the factory code.</param>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryPathFactory(
            CodeBuilder builder,
            CanvasGeometry.Path obj,
            string typeName,
            string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.RoundedRectangle factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="obj">Describes the object that should be instantiated by the factory code.</param>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryRoundedRectangleFactory(
            CodeBuilder builder,
            CanvasGeometry.RoundedRectangle obj,
            string typeName,
            string fieldName);

        /// <summary>
        /// Writes CanvasGeometery.TransformedGeometry factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="obj">Describes the object that should be instantiated by the factory code.</param>
        /// <param name="typeName">The type of the result.</param>
        /// <param name="fieldName">If not null, the name of the field in which the result is stored.</param>
        protected abstract void WriteCanvasGeometryTransformedGeometryFactory(
            CodeBuilder builder,
            CanvasGeometry.TransformedGeometry obj,
            string typeName,
            string fieldName);

        /// <summary>
        /// Write the CompositeEffect factory code.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="compositeEffect">Composite effect object.</param>
        /// <returns>String that should be used as the parameter for CreateEffectFactory.</returns>
        protected abstract string WriteCompositeEffectFactory(
            CodeBuilder builder,
            Mgce.CompositeEffect compositeEffect);

        /// <summary>
        /// Write a Bytes field.
        /// </summary>
        /// <param name="builder">A <see cref="CodeBuilder"/> used to create the code.</param>
        /// <param name="fieldName">The name of the Bytes field to be written.</param>
        protected void WriteBytesField(CodeBuilder builder, string fieldName)
        {
            builder.WriteLine($"{_stringifier.Static} {_stringifier.Readonly(_stringifier.ReferenceTypeName(_stringifier.ByteArray))} {fieldName} = {_stringifier.New(_stringifier.ByteArray)}");
        }

        /// <summary>
        /// Writes code that initializes a theme property value in the theme property set.
        /// </summary>
        protected void WriteThemePropertyInitialization(
            CodeBuilder builder,
            string propertySetVariableName,
            PropertyBinding prop)
            => WriteThemePropertyInitialization(
                builder,
                propertySetVariableName,
                prop,
                $"_theme{prop.Name}");

        /// <summary>
        /// Writes code that initializes a theme property value in the theme property set.
        /// </summary>
        protected void WriteThemePropertyInitialization(
            CodeBuilder builder,
            string propertySetVariableName,
            PropertyBinding prop,
            string themePropertyAccessor)
        {
            var propertyValueAccessor = GetThemePropertyAccessor(themePropertyAccessor, prop);
            builder.WriteLine($"{propertySetVariableName}{Deref}Insert{PropertySetValueTypeName(prop.ActualType)}({String(prop.Name)}, {propertyValueAccessor});");
        }

        /// <summary>
        /// Gets code to access a theme property.
        /// </summary>
        /// <returns>
        /// An expression that gets a theme property value.
        /// </returns>
        protected string GetThemePropertyAccessor(string accessor, PropertyBinding prop)
            => prop.ExposedType switch
            {
                // Colors are stored as Vector4 because Composition cannot animate
                // subchannels of colors.
                // The cast to Color is necessary if the accessor returns Object (for
                // example if the value is coming from a DependencyPropertyChangedEventArgs.
                PropertySetValueType.Color => $"ColorAsVector4((Color){accessor})",

                // Scalars are stored as float, but exposed as double because
                // XAML markup prefers floats.
                PropertySetValueType.Scalar => $"(float){accessor}",
                _ => accessor,
            };

        void WritePropertySetInitialization(CodeBuilder builder, CompositionPropertySet propertySet, string variableName)
        {
            foreach (var (name, type) in propertySet.Names)
            {
                var valueInitializer = PropertySetValueInitializer(propertySet, name, type);
                builder.WriteLine($"{variableName}{Deref}Insert{PropertySetValueTypeName(type)}({String(name)}, {valueInitializer});");
            }
        }

        /// <summary>
        /// Returns text that describes the contents of the source metadata.
        /// </summary>
        /// <returns>A list of strings describing the source.</returns>
        protected IEnumerable<string> GetSourceDescriptionLines()
        {
            // Describe the source. Currently this handles only Lottie sources.
            var metadata = _sourceMetadata.LottieMetadata;
            if (metadata != null)
            {
                if (metadata.CompositionName != null)
                {
                    yield return $"Name:        {metadata.CompositionName}";
                }

                yield return $"Frame rate:  {metadata.FramesPerSecond} fps";
                yield return $"Frame count: {metadata.DurationInFrames}";
                if (metadata.Markers.Any())
                {
                    yield return "===========";
                    yield return "Segments (aka markers):";
                    foreach (var (name, start, end) in metadata.Markers)
                    {
                        var durationMs = (end.time - start.time).TotalMilliseconds;
                        var duration = durationMs == 0 ? string.Empty : $"{durationMs}mS";
                        var playCommand = start.time == end.time
                                ? $"player{Deref}SetProgress({start.progress})"
                                : $"player{Deref}PlayAsync({start.progress}, {end.progress}, _)";

                        yield return $"{String(name),-12} {duration,6}  {playCommand}";
                    }
                }
            }

            // If there are property bindings, output information about them.
            // But only do this if we're NOT generating a DependencyObject because
            // the property bindings available on a DependencyObject are obvious
            // from the code and repeating them here would just be noise.
            var names = _sourceMetadata.PropertyBindings;
            if (!_generateDependencyObject && names?.Any() == true)
            {
                yield return "===========";
                yield return "Property bindings:";
                foreach (var entry in names)
                {
                    if (entry.ActualType != entry.ExposedType)
                    {
                        yield return $"{PropertySetValueTypeName(entry.ExposedType),-8} {String(entry.Name),-15} as {PropertySetValueTypeName(entry.ActualType)}";
                    }
                    else
                    {
                        yield return $"{PropertySetValueTypeName(entry.ActualType),-8} {String(entry.Name),-15}";
                    }
                }
            }
        }

        /// <summary>
        /// Call this to get a list of the asset files referenced by the generated code.
        /// </summary>
        /// <returns>
        /// List of asset files and their relative path to the Asset folder in a UWP that are referenced by the generated code.
        /// An item in the returned list has format "ms-appx:///Assets/subFolder/fileName", which the generated code
        /// will use to load the file from.
        /// </returns>
        protected IReadOnlyList<Uri> GetAssetsList() => _loadedImageSurfaceInfos.Where(n => n.ImageUri != null).Select(n => n.ImageUri).ToArray();

        /// <summary>
        /// Call this to generate the code. Returns a string containing the generated code.
        /// </summary>
        /// <returns>The code.</returns>
        protected string GenerateCode()
        {
            var builder = new CodeBuilder();

            // Write the auto-generated warning comment.
            foreach (var line in AutoGeneratedHeaderText)
            {
                builder.WriteLine(line);
            }

            // Write the start of the file. This is everything up to the start of the AnimatedVisual class.
            WriteFileStart(builder);

            // Write the LoadedImageSurface byte arrays into the outer (IAnimatedVisualSource) class.
            WriteLoadedImageSurfaceArrays(builder);

            // Write the method that starts an animation and binds its AnimationController.Progress to an expression.
            WriteHelperStartProgressBoundAnimation(builder);

            // Write each AnimatedVisual class.
            var firstAnimatedVisualWritten = false;
            foreach (var animatedVisualGenerator in _animatedVisualGenerators)
            {
                if (firstAnimatedVisualWritten)
                {
                    // Put a blank line between each AnimatedVisual class.
                    builder.WriteLine();
                }

                animatedVisualGenerator.WriteAnimatedVisualCode(builder);
                firstAnimatedVisualWritten = true;
            }

            // Write the end of the file.
            WriteFileEnd(builder);

            return builder.ToString();
        }

        /// <summary>
        /// Returns the code to call the factory for the given object.
        /// </summary>
        /// <returns>The code to call the factory for the given object.</returns>
        protected string CallFactoryFor(CanvasGeometry obj) => _currentAnimatedVisualGenerator.CallFactoryFor(obj);

        // Makes the given name suitable for use as a class name in languages such as C# and C++.
        static string SanitizeTypeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // If the first character is not a letter, prepend an underscore.
            if (!char.IsLetter(name, 0))
            {
                name = "_" + name;
            }

            // Replace any disallowed character with underscores.
            name =
                new string((from ch in name
                            select char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

            // Remove any duplicated underscores.
            name = name.Replace("__", "_");

            // Capitalize the first letter.
            name = name.ToUpperInvariant().Substring(0, 1) + name.Substring(1);

            return name;
        }

        protected void WriteInitializedField(CodeBuilder builder, string typeName, string fieldName, string initialization)
            => builder.WriteLine($"{typeName} {fieldName}{initialization};");

        void WriteDefaultInitializedField(CodeBuilder builder, string typeName, string fieldName)
            => WriteInitializedField(builder, typeName, fieldName, _stringifier.DefaultInitialize);

        // Returns true iff the given sequence has exactly one item in it.
        // This is equivalent to Count() == 1, but doesn't require the whole
        // sequence to be enumerated.
        static bool IsEqualToOne<T>(IEnumerable<T> items)
        {
            var seenOne = false;
            foreach (var item in items)
            {
                if (seenOne)
                {
                    // Already seen one item - the sequence has more than one item.
                    return false;
                }

                seenOne = true;
            }

            return seenOne;
        }

        // Returns true iff the given sequence has more than one item in it.
        // This is equivalent to Count() > 1, but doesn't require the whole
        // sequence to be enumerated.
        static bool IsGreaterThanOne<T>(IEnumerable<T> items)
        {
            var seenOne = false;
            foreach (var item in items)
            {
                if (seenOne)
                {
                    // Already seen one item - the sequence has at least one item.
                    return true;
                }

                seenOne = true;
            }

            // The sequence is empty.
            return false;
        }

        // The InReferences on a node are used to determine whether a node needs storage (it does
        // if multiple other nodes reference it), however in one case a node with multiple
        // InReferences does not need storage:
        // * If the references are only from an ExpressionAnimation that is created in the factory
        //   for the node.
        // This method gets the InReferences, filtering out those which can be ignored.
        static IEnumerable<ObjectData> FilteredInRefs(ObjectData node)
        {
            // Examine all of the inrefs to the node.
            foreach (var vertex in node.InReferences)
            {
                var from = vertex.Node;

                // If the inref is from an ExpressionAnimation ...
                if (from.Object is ExpressionAnimation exprAnim)
                {
                    // ... is the animation shared?
                    if (from.InReferences.Length > 1)
                    {
                        yield return from;
                        continue;
                    }

                    // ... is the animation animating a property on the current node or its property set?
                    var isExpressionOnThisNode = false;

                    var compObject = (CompositionObject)node.Object;

                    // Search the animators to find the animator for this ExpressionAnimation.
                    // It will be found iff the ExpressionAnimation is animating this node.
                    foreach (var animator in compObject.Animators.Concat(compObject.Properties.Animators))
                    {
                        if (animator.Animation is ExpressionAnimation animatorExpression &&
                            animatorExpression.Expression == exprAnim.Expression)
                        {
                            isExpressionOnThisNode = true;
                            break;
                        }
                    }

                    if (!isExpressionOnThisNode)
                    {
                        yield return from;
                    }
                }
                else
                {
                    yield return from;
                }
            }
        }

        string String(GenericDataObject value)
        {
            switch (value.Type)
            {
                case GenericDataObjectType.Bool: return _stringifier.Bool(((GenericDataBool)value).Value);
                case GenericDataObjectType.Number: return _stringifier.Double(((GenericDataNumber)value).Value);
                case GenericDataObjectType.String: return _stringifier.String(((GenericDataString)value).Value);
                default: throw new InvalidOperationException();
            }
        }

        string Deref => _stringifier.Deref;

        string ConstVar => _stringifier.ConstVar;

        string New(string typeName) => _stringifier.New(typeName);

        string ReferenceTypeName(string value) => _stringifier.ReferenceTypeName(value);

        string Static => _stringifier.Static;

        string IAnimatedVisualSourceInfo.ClassName => _className;

        string IAnimatedVisualSourceInfo.Namespace => _namespace;

        string IAnimatedVisualSourceInfo.Interface => _interfaceType;

        string IAnimatedVisualSourceInfo.ReusableExpressionAnimationFieldName => SingletonExpressionAnimationName;

        string IAnimatedVisualSourceInfo.DurationTicksFieldName => DurationTicksFieldName;

        bool IAnimatedVisualSourceInfo.GenerateDependencyObject => _generateDependencyObject;

        string IAnimatedVisualSourceInfo.ThemePropertiesFieldName => ThemePropertiesFieldName;

        bool IAnimatedVisualSourceInfo.IsThemed => _isThemed;

        Vector2 IAnimatedVisualSourceInfo.CompositionDeclaredSize => _compositionDeclaredSize;

        bool IAnimatedVisualSourceInfo.UsesCanvas => _animatedVisualGenerators.Any(f => f.UsesCanvas);

        bool IAnimatedVisualSourceInfo.UsesCanvasEffects => _animatedVisualGenerators.Any(f => f.UsesCanvasEffects);

        bool IAnimatedVisualSourceInfo.UsesCanvasGeometry => _animatedVisualGenerators.Any(f => f.UsesCanvasGeometry);

        bool IAnimatedVisualSourceInfo.UsesNamespaceWindowsUIXamlMedia => _animatedVisualGenerators.Any(f => f.UsesNamespaceWindowsUIXamlMedia);

        bool IAnimatedVisualSourceInfo.UsesStreams => _animatedVisualGenerators.Any(f => f.UsesStreams);

        IReadOnlyList<IAnimatedVisualInfo> IAnimatedVisualSourceInfo.AnimatedVisualInfos => _animatedVisualGenerators;

        bool IAnimatedVisualSourceInfo.UsesCompositeEffect => _animatedVisualGenerators.Any(f => f.UsesCompositeEffect);

        IReadOnlyList<LoadedImageSurfaceInfo> IAnimatedVisualSourceInfo.LoadedImageSurfaces => _loadedImageSurfaceInfos;

        SourceMetadata IAnimatedVisualSourceInfo.SourceMetadata => _sourceMetadata;

        // Writes code that will return the given GenericDataMap as Windows.Data.Json.
        void WriteJsonFactory(CodeBuilder builder, GenericDataMap jsonData, string factoryName)
        {
            builder.WriteLine($"{_stringifier.ReferenceTypeName("JsonObject")} {factoryName}()");
            builder.OpenScope();
            builder.WriteLine($"{_stringifier.Var} result = {New("JsonObject")}();");
            WritePopulateJsonObject(builder, jsonData, "result", 0);
            builder.WriteLine($"return result;");
            builder.CloseScope();
            builder.WriteLine();
        }

        void WritePopulateJsonArray(CodeBuilder builder, GenericDataList jsonData, string arrayName, int recursionLevel)
        {
            foreach (var value in jsonData)
            {
                if (value is null)
                {
                    builder.WriteLine($"{arrayName}{Deref}Append(JsonValue{Deref}CreateNullValue());");
                }
                else
                {
                    switch (value.Type)
                    {
                        case GenericDataObjectType.Bool:
                            builder.WriteLine($"{arrayName}{Deref}Append(JsonValue{Deref}CreateBooleanValue({String(value)}));");
                            break;
                        case GenericDataObjectType.Number:
                            builder.WriteLine($"{arrayName}{Deref}Append(JsonValue{Deref}CreateNumberValue({String(value)}));");
                            break;
                        case GenericDataObjectType.String:
                            builder.WriteLine($"{arrayName}{Deref}Append(JsonValue{Deref}CreateStringValue({String(value)}));");
                            break;
                        case GenericDataObjectType.List:
                            if (((GenericDataList)value).Count == 0)
                            {
                                builder.WriteLine($"{arrayName}{Deref}Append({New("JsonArray")}());");
                            }
                            else
                            {
                                var subArrayName = $"jarray_{recursionLevel}";
                                builder.OpenScope();
                                builder.WriteLine($"{_stringifier.Var} {subArrayName} = {New("JsonArray")}();");
                                builder.WriteLine($"result{Deref}Append({subArrayName});");
                                WritePopulateJsonArray(builder, (GenericDataList)value, subArrayName, recursionLevel + 1);
                                builder.CloseScope();
                            }

                            break;
                        case GenericDataObjectType.Map:
                            if (((GenericDataMap)value).Count == 0)
                            {
                                builder.WriteLine($"{arrayName}{Deref}Append({New("JsonObject")}());");
                            }
                            else
                            {
                                var subObjectName = $"jobject_{recursionLevel}";
                                builder.OpenScope();
                                builder.WriteLine($"{_stringifier.Var} {subObjectName} = {New("JsonObject")}();");
                                builder.WriteLine($"result{Deref}Append({subObjectName});");
                                WritePopulateJsonObject(builder, (GenericDataMap)value, subObjectName, recursionLevel + 1);
                                builder.CloseScope();
                            }

                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
        }

        void WritePopulateJsonObject(CodeBuilder builder, GenericDataMap jsonData, string objectName, int recursionLevel)
        {
            foreach (var pair in jsonData)
            {
                var k = _stringifier.String(pair.Key);
                var value = pair.Value;

                if (value is null)
                {
                    builder.WriteLine($"{objectName}{Deref}Add({k}, JsonValue{Deref}CreateNullValue());");
                }
                else
                {
                    switch (value.Type)
                    {
                        case GenericDataObjectType.Bool:
                            builder.WriteLine($"{objectName}{Deref}Add({k}, JsonValue{Deref}CreateBooleanValue({String(value)}));");
                            break;
                        case GenericDataObjectType.Number:
                            builder.WriteLine($"{objectName}{Deref}Add({k}, JsonValue{Deref}CreateNumberValue({String(value)}));");
                            break;
                        case GenericDataObjectType.String:
                            builder.WriteLine($"{objectName}{Deref}Add({k}, JsonValue{Deref}CreateStringValue({String(value)}));");
                            break;
                        case GenericDataObjectType.List:
                            if (((GenericDataList)value).Count == 0)
                            {
                                builder.WriteLine($"{objectName}{Deref}Add({k}, {New("JsonArray")}());");
                            }
                            else
                            {
                                var subArrayName = $"jarray_{recursionLevel}";
                                builder.OpenScope();
                                builder.WriteLine($"{_stringifier.Var} {subArrayName} = {New("JsonArray")}();");
                                builder.WriteLine($"result{Deref}Add({k}, {subArrayName});");
                                WritePopulateJsonArray(builder, (GenericDataList)value, subArrayName, recursionLevel + 1);
                                builder.CloseScope();
                            }

                            break;
                        case GenericDataObjectType.Map:
                            if (((GenericDataMap)value).Count == 0)
                            {
                                builder.WriteLine($"{objectName}{Deref}Add({k}, {New("JsonObject")}());");
                            }
                            else
                            {
                                var subObjectName = $"jobject_{recursionLevel}";
                                builder.OpenScope();
                                builder.WriteLine($"{_stringifier.Var} {subObjectName} = {New("JsonObject")}();");
                                builder.WriteLine($"result{Deref}Add({k}, {subObjectName});");
                                WritePopulateJsonObject(builder, (GenericDataMap)value, subObjectName, recursionLevel + 1);
                                builder.CloseScope();
                            }

                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
        }

        // Write the LoadedImageSurface byte arrays into the outer (IAnimatedVisualSource) class.
        void WriteLoadedImageSurfaceArrays(CodeBuilder builder)
        {
            bool bytesWritten = false;

            foreach (var loadedImageSurface in _loadedImageSurfaceInfos)
            {
                if (loadedImageSurface.Bytes != null)
                {
                    WriteBytesField(builder, loadedImageSurface.BytesFieldName);
                    builder.OpenScope();
                    builder.BytesToLiteral(loadedImageSurface.Bytes, maximumColumns: 100);
                    builder.UnIndent();
                    builder.WriteLine("};");
                    bytesWritten = true;
                }
            }

            if (bytesWritten)
            {
                builder.WriteLine();
            }
        }

        static LoadedImageSurfaceInfo LoadedImageSurfaceInfoFromObjectData(ObjectData node)
        {
            if (!node.IsLoadedImageSurface)
            {
                throw new InvalidOperationException();
            }

            var bytes = (node.Object as Wmd.LoadedImageSurfaceFromStream)?.Bytes;
            return new LoadedImageSurfaceInfo(
                                node.TypeName,
                                node.Name,
                                node.FieldName,
                                node.LoadedImageSurfaceBytesFieldName,
                                node.LoadedImageSurfaceImageUri,
                                ((Wmd.LoadedImageSurface)node.Object).Type,
                                bytes: bytes);
        }

        // Orders nodes by their name using alpha-numeric ordering (which is the most natural ordering for code
        // names that contain embedded numbers).
        static IEnumerable<ObjectData> OrderByName(IEnumerable<ObjectData> nodes) =>
            nodes.OrderBy(n => n.Name, AlphanumericStringComparer.Instance);

        static string PropertySetValueTypeName(PropertySetValueType value)
            => value switch
            {
                PropertySetValueType.Color => "Color",
                PropertySetValueType.Scalar => "Scalar",
                PropertySetValueType.Vector2 => "Vector2",
                PropertySetValueType.Vector3 => "Vector3",
                PropertySetValueType.Vector4 => "Vector4",
                _ => throw new InvalidOperationException(),
            };

        string PropertySetValueInitializer(CompositionPropertySet propertySet, string propertyName, PropertySetValueType propertyType)
            => propertyType switch
            {
                PropertySetValueType.Color => PropertySetColorValueInitializer(propertySet, propertyName),
                PropertySetValueType.Scalar => PropertySetScalarValueInitializer(propertySet, propertyName),
                PropertySetValueType.Vector2 => PropertySetVector2ValueInitializer(propertySet, propertyName),
                PropertySetValueType.Vector3 => PropertySetVector3ValueInitializer(propertySet, propertyName),
                PropertySetValueType.Vector4 => PropertySetVector4ValueInitializer(propertySet, propertyName),
                _ => throw new InvalidOperationException(),
            };

        string PropertySetColorValueInitializer(CompositionPropertySet propertySet, string propertyName)
            => propertySet.TryGetColor(propertyName, out var value) == CompositionGetValueStatus.Succeeded
                    ? _stringifier.Color(value)
                    : throw new InvalidOperationException();

        string PropertySetScalarValueInitializer(CompositionPropertySet propertySet, string propertyName)
            => propertySet.TryGetScalar(propertyName, out var value) == CompositionGetValueStatus.Succeeded
                    ? _stringifier.Float(value)
                    : throw new InvalidOperationException();

        string PropertySetVector2ValueInitializer(CompositionPropertySet propertySet, string propertyName)
            => propertySet.TryGetVector2(propertyName, out var value) == CompositionGetValueStatus.Succeeded
                    ? _stringifier.Vector2(value)
                    : throw new InvalidOperationException();

        string PropertySetVector3ValueInitializer(CompositionPropertySet propertySet, string propertyName)
            => propertySet.TryGetVector3(propertyName, out var value) == CompositionGetValueStatus.Succeeded
                    ? _stringifier.Vector3(value)
                    : throw new InvalidOperationException();

        string PropertySetVector4ValueInitializer(CompositionPropertySet propertySet, string propertyName)
            => propertySet.TryGetVector4(propertyName, out var value) == CompositionGetValueStatus.Succeeded
                    ? _stringifier.Vector4(value)
                    : throw new InvalidOperationException();

        // Writes a static method that starts an animation, then binds the Progress property of its
        // AnimationController for that animation to an expression. This is used to start animations
        // that have their progress bound to the progress of another property.
        void WriteHelperStartProgressBoundAnimation(CodeBuilder builder)
        {
            builder.WriteLine($"{Static} void StartProgressBoundAnimation(");
            builder.Indent();
            builder.WriteLine($"{ReferenceTypeName("CompositionObject")} target,");
            builder.WriteLine($"{_stringifier.StringType} animatedPropertyName,");
            builder.WriteLine($"{ReferenceTypeName("CompositionAnimation")} animation,");
            builder.WriteLine($"{ReferenceTypeName("ExpressionAnimation")} controllerProgressExpression)");
            builder.UnIndent();
            builder.OpenScope();
            builder.WriteLine($"target{Deref}StartAnimation(animatedPropertyName, animation);");
            builder.WriteLine($"{ConstVar} controller = target{Deref}TryGetAnimationController(animatedPropertyName);");
            builder.WriteLine($"controller{Deref}Pause();");
            builder.WriteLine($"controller{Deref}StartAnimation({String("Progress")}, controllerProgressExpression);");
            builder.CloseScope();
            builder.WriteLine();
        }

        /// <summary>
        /// Generates an IAnimatedVisual implementation.
        /// </summary>
        sealed class AnimatedVisualGenerator : IAnimatedVisualInfo
        {
            readonly HashSet<(ObjectData, ObjectData)> _factoriesAlreadyCalled = new HashSet<(ObjectData, ObjectData)>();
            readonly InstantiatorGeneratorBase _owner;
            readonly Stringifier _stringifier;
            readonly ObjectData _rootNode;
            readonly ObjectGraph<ObjectData> _objectGraph;
            readonly uint _requiredUapVersion;
            readonly bool _isPartOfMultiVersionSource;

            // The subset of the object graph for which factories will be generated.
            readonly ObjectData[] _nodes;

            IReadOnlyList<LoadedImageSurfaceInfo> _loadedImageSurfaceInfos;

            // Holds the node for which a factory is currently being written.
            ObjectData _currentObjectFactoryNode;

            internal AnimatedVisualGenerator(
                InstantiatorGeneratorBase owner,
                CompositionObject graphRoot,
                uint requiredUapVersion,
                bool isPartOfMultiVersionSource)
            {
                _owner = owner;
                _stringifier = _owner._stringifier;
                _requiredUapVersion = requiredUapVersion;
                _isPartOfMultiVersionSource = isPartOfMultiVersionSource;

                // Build the object graph.
                _objectGraph = ObjectGraph<ObjectData>.FromCompositionObject(graphRoot, includeVertices: true);

                // Force inlining on CompositionPath nodes that are only referenced once, because their factories
                // are always very simple.
                foreach (var node in _objectGraph.Nodes.Where(
                                        n => n.Type == Graph.NodeType.CompositionPath &&
                                        IsEqualToOne(FilteredInRefs(n))))
                {
                    node.ForceInline(() =>
                    {
                        var inlinedFactoryCode = CallFactoryFromFor(node, ((CompositionPath)node.Object).Source);
                        return $"{New("CompositionPath")}({_stringifier.FactoryCall(inlinedFactoryCode)})";
                    });
                }

                // Force inlining on CubicBezierEasingFunction nodes that are only referenced once, because their factories
                // are always very simple.
                foreach (var (node, obj) in _objectGraph.CompositionObjectNodes.Where(
                                        n => n.Object is CubicBezierEasingFunction &&
                                            IsEqualToOne(FilteredInRefs(n.Node))))
                {
                    node.ForceInline(() =>
                    {
                        return CallCreateCubicBezierEasingFunction((CubicBezierEasingFunction)node.Object);
                    });
                }

                // If there is a theme property set, give it a special name and
                // mark it as shared. The theme property set is the only unowned property set.
                foreach (var (node, obj) in _objectGraph.CompositionObjectNodes.Where(
                        n => n.Object is CompositionPropertySet cps && cps.Owner == null))
                {
                    node.Name = "ThemeProperties";
                    node.IsSharedNode = true;

                    // If there's a theme property set, this IAnimatedVisual is themed.
                    IsThemed = true;
                }

                // Mark all the LoadedImageSurface nodes as shared and ensure they have storage.
                foreach (var (node, _) in _objectGraph.LoadedImageSurfaceNodes)
                {
                    node.IsSharedNode = true;
                }

                // Get the nodes that will produce factory methods.
                var factoryNodes = _objectGraph.Nodes.Where(n => n.NeedsAFactory).ToArray();

                // Give names to each node, except the nodes that may be shared by multiple IAnimatedVisuals.
                foreach ((var n, var name) in NodeNamer<ObjectData>.GenerateNodeNames(factoryNodes.Where(n => !n.IsSharedNode)))
                {
                    n.Name = name;
                }

                // Force storage to be allocated for nodes that have multiple references to them,
                // or are LoadedImageSurfaces.
                foreach (var node in _objectGraph.Nodes)
                {
                    if (node.IsSharedNode)
                    {
                        // Shared nodes are cached and shared between IAnimatedVisual instances, so
                        // they require storage.
                        node.RequiresStorage = true;
                        node.RequiresReadonlyStorage = true;
                    }
                    else if (IsGreaterThanOne(FilteredInRefs(node)))
                    {
                        // Node is referenced more than once so it requires storage.
                        if (node.Object is CompositionPropertySet propertySet)
                        {
                            // The node is a CompositionPropertySet. Rather than storing
                            // it, store the owner of the CompositionPropertySet. The
                            // CompositionPropertySet can be reached from its owner.
                            if (propertySet.Owner != null)
                            {
                                var propertySetOwner = NodeFor(propertySet.Owner);
                                propertySetOwner.RequiresStorage = true;
                            }
                        }
                        else
                        {
                            node.RequiresStorage = true;
                        }
                    }
                }

                // Find the root node.
                _rootNode = NodeFor(graphRoot);

                // Ensure the root object has storage because it is referenced from IAnimatedVisual::RootVisual.
                _rootNode.RequiresStorage = true;

                // Save the nodes, ordered by name.
                _nodes = OrderByName(factoryNodes).ToArray();
            }

            // Returns the node for the theme CompositionPropertySet, or null if the
            // IAnimatedVisual does not support theming.
            internal bool IsThemed { get; }

            // Returns the nodes that are shared between multiple IAnimatedVisuals.
            // The fields for these are stored on the IAnimatedVisualSource.
            internal IEnumerable<ObjectData> GetSharedNodes() => _objectGraph.Nodes.Where(n => n.IsSharedNode);

            // Returns the node for the given object.
            ObjectData NodeFor(CompositionObject obj) => _objectGraph[obj];

            ObjectData NodeFor(CompositionPath obj) => _objectGraph[obj];

            ObjectData NodeFor(Wg.IGeometrySource2D obj) => _objectGraph[obj];

            ObjectData NodeFor(Wmd.LoadedImageSurface obj) => _objectGraph[obj];

            internal bool UsesCanvas => _nodes.Where(n => n.UsesCanvas).Any();

            internal bool UsesCanvasEffects => _nodes.Where(n => n.UsesCanvasEffects).Any();

            internal bool UsesCanvasGeometry => _nodes.Where(n => n.UsesCanvasGeometry).Any();

            internal bool UsesNamespaceWindowsUIXamlMedia => _nodes.Where(n => n.UsesNamespaceWindowsUIXamlMedia).Any();

            internal bool UsesStreams => _nodes.Where(n => n.UsesStream).Any();

            internal bool HasLoadedImageSurface => _nodes.Where(n => n.IsLoadedImageSurface).Any();

            internal bool UsesCompositeEffect => _nodes.Where(n => n.UsesCompositeEffect).Any();

            string ConstExprField(string type, string name, string value) => _stringifier.ConstExprField(type, name, value);

            string Deref => _stringifier.Deref;

            string New(string typeName) => _stringifier.New(typeName);

            string Null => _stringifier.Null;

            string ReferenceTypeName(string value) => _stringifier.ReferenceTypeName(value);

            string ConstVar => _stringifier.ConstVar;

            string Bool(bool value) => _stringifier.Bool(value);

            string Color(Wui.Color value) => _stringifier.Color(value);

            string IListAdd => _stringifier.IListAdd;

            string Float(float value) => _stringifier.Float(value);

            string Int(int value) => _stringifier.Int32(value);

            string Matrix3x2(Sn.Matrix3x2 value) => _stringifier.Matrix3x2(value);

            string Matrix4x4(Matrix4x4 value) => _stringifier.Matrix4x4(value);

            // readonly on C#, const on C++.
            string Readonly(string value) => _stringifier.Readonly(value);

            string String(WinCompData.Expressions.Expression value) => String(value.ToText());

            string String(string value) => _stringifier.String(value);

            string Vector2(Sn.Vector2 value) => _stringifier.Vector2(value);

            string Vector3(Sn.Vector3 value) => _stringifier.Vector3(value);

            string Vector4(Sn.Vector4 value) => _stringifier.Vector4(value);

            string BorderMode(CompositionBorderMode value) => _stringifier.BorderMode(value);

            string ColorSpace(CompositionColorSpace value) => _stringifier.ColorSpace(value);

            string ExtendMode(CompositionGradientExtendMode value) => _stringifier.ExtendMode(value);

            string MappingMode(CompositionMappingMode value) => _stringifier.MappingMode(value);

            string StrokeCap(CompositionStrokeCap value) => _stringifier.StrokeCap(value);

            string StrokeLineJoin(CompositionStrokeLineJoin value) => _stringifier.StrokeLineJoin(value);

            string TimeSpan(TimeSpan value) => value == _owner._compositionDuration ? _stringifier.TimeSpan(DurationTicksFieldName) : _stringifier.TimeSpan(value);

            /// <summary>
            /// Returns the code to call the factory for the given object.
            /// </summary>
            /// <returns>The code to call the factory for the given object.</returns>
            internal string CallFactoryFor(CanvasGeometry obj)
                => CallFactoryFromFor(_currentObjectFactoryNode, obj);

            // Returns the code to call the factory for the given node from the given node.
            string CallFactoryFromFor(ObjectData callerNode, ObjectData calleeNode)
            {
                if (callerNode.CallFactoryFromForCache.TryGetValue(calleeNode, out string result))
                {
                    // Return the factory from the cache.
                    return result;
                }

                // Get the factory call code.
                result = CallFactoryFromFor_UnCached(callerNode, calleeNode);

                // Save the factory call code in the cache on the caller for next time.
                if (calleeNode.RequiresStorage && !_owner._disableFieldOptimization)
                {
                    // The node has storage for its result. Next time just return the field.
                    callerNode.CallFactoryFromForCache.Add(calleeNode, calleeNode.FieldName);
                }
                else
                {
                    callerNode.CallFactoryFromForCache.Add(calleeNode, result);
                }

                return result;
            }

            // Returns the code to call the factory for the given node from the given node.
            string CallFactoryFromFor_UnCached(ObjectData callerNode, ObjectData calleeNode)
            {
                // Calling into the root node is handled specially. The root node is always
                // created before the first vertex to it, so it is sufficient to just get
                // it from its field.
                if (calleeNode == _rootNode)
                {
                    Debug.Assert(calleeNode.RequiresStorage, "Root node is not stored in a field");
                    return calleeNode.FieldName;
                }

                if (_owner._disableFieldOptimization)
                {
                    // When field optimization is disabled, always return a call to the factory.
                    // If the factory has been called already, it will return the value from
                    // its storage.
                    return calleeNode.FactoryCall();
                }

                if (calleeNode.Object is CompositionPropertySet propertySet)
                {
                    // CompositionPropertySets do not have factories unless they are
                    // unowned. The call to the factory is therefore a call to the owner's
                    // factory, then a dereference of the ".Properties" property on the owner.
                    if (propertySet.Owner != null)
                    {
                        return $"{CallFactoryFromFor(callerNode, NodeFor(propertySet.Owner))}{Deref}Properties";
                    }
                }

                // Find the vertex from caller to callee.
                var firstVertexFromCallerToCallee =
                        (from inref in calleeNode.InReferences
                         where inref.Node == callerNode
                         orderby inref.Position
                         select inref).FirstOrDefault();

                if (firstVertexFromCallerToCallee.Node is null &&
                    calleeNode.Object is CompositionObject calleeCompositionObject)
                {
                    // Didn't find a reference from caller to callee. The reference may be to
                    // the property set of the callee.
                    var propertySetNode = NodeFor(calleeCompositionObject.Properties);
                    firstVertexFromCallerToCallee =
                        (from inref in propertySetNode.InReferences
                         where inref.Node == callerNode
                         orderby inref.Position
                         select inref).First();
                }

                // Find the first vertex to the callee from any caller.
                var firstVertexToCallee = calleeNode.InReferences.First();

                // If the object has a vertex with a lower position then the object
                // will have already been created by the time the caller needs the object.
                if (firstVertexToCallee.Position < firstVertexFromCallerToCallee.Position)
                {
                    // The object was created by another caller. Just access the field.
                    Debug.Assert(calleeNode.RequiresStorage, "Expecting to access a field containing a previously cached value, but the callee has no field");
                    return calleeNode.FieldName;
                }
                else if (calleeNode.RequiresStorage && _factoriesAlreadyCalled.Contains((callerNode, calleeNode)))
                {
                    return calleeNode.FieldName;
                }
                else
                {
                    // Keep track of the fact that the caller called the factory
                    // already. If the caller asks for the factory twice and the factory
                    // does not have a cache, then the caller was expected to store the
                    // result in a local.
                    // NOTE: currently there is no generated code that is known to hit this case,
                    // so this is just here to ensure we find it if it happens.
                    if (!_factoriesAlreadyCalled.Add((callerNode, calleeNode)))
                    {
                        throw new InvalidOperationException();
                    }

                    return calleeNode.FactoryCall();
                }
            }

            // Returns the code to call the factory for the given object from the given node.
            string CallFactoryFromFor(ObjectData callerNode, CompositionObject obj) => CallFactoryFromFor(callerNode, NodeFor(obj));

            string CallFactoryFromFor(ObjectData callerNode, CompositionPath obj) => CallFactoryFromFor(callerNode, NodeFor(obj));

            string CallFactoryFromFor(ObjectData callerNode, Wg.IGeometrySource2D obj) => CallFactoryFromFor(callerNode, NodeFor(obj));

            bool GenerateCompositionPathFactory(CodeBuilder builder, CompositionPath obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                var canvasGeometry = _objectGraph[(CanvasGeometry)obj.Source];
                WriteCreateAssignment(builder, node, $"{New("CompositionPath")}({_stringifier.FactoryCall(canvasGeometry.FactoryCall())})");
                WriteObjectFactoryEnd(builder);
                return true;
            }

            void WriteObjectFactoryStart(CodeBuilder builder, ObjectData node, IEnumerable<string> parameters = null)
            {
                // Save the node as the current node while the factory is being written.
                _currentObjectFactoryNode = node;
                builder.WriteComment(node.LongComment);

                // Write the signature of the method.
                builder.WriteLine($"{_owner._stringifier.ReferenceTypeName(node.TypeName)} {node.Name}({(parameters == null ? string.Empty : string.Join(", ", parameters))})");
                builder.OpenScope();
            }

            void WriteObjectFactoryEnd(CodeBuilder builder)
            {
                builder.WriteLine("return result;");
                builder.CloseScope();
                builder.WriteLine();
                _currentObjectFactoryNode = null;
            }

            // Writes a factory that just creates an object but doesn't parameterize it before it is returned.
            void WriteSimpleObjectFactory(CodeBuilder builder, ObjectData node, string createCallText)
            {
                WriteObjectFactoryStart(builder, node);
                if (node.RequiresStorage)
                {
                    if (_owner._disableFieldOptimization)
                    {
                        // Create the object unless it has already been created.
                        builder.WriteLine($"return ({node.FieldName} == {Null})");
                        builder.Indent();
                        builder.WriteLine($"? {node.FieldName} = {createCallText}");
                        builder.WriteLine($": {node.FieldName};");
                        builder.UnIndent();
                    }
                    else
                    {
                        // If field optimization is enabled, the method will only get called once.
                        builder.WriteLine($"return {node.FieldName} = {createCallText};");
                    }
                }
                else
                {
                    // The object is only used once.
                    builder.WriteLine($"return {createCallText};");
                }

                builder.CloseScope();
                builder.WriteLine();
                _currentObjectFactoryNode = null;
            }

            void WriteCreateAssignment(CodeBuilder builder, ObjectData node, string createCallText)
            {
                if (node.RequiresStorage)
                {
                    if (_owner._disableFieldOptimization)
                    {
                        // If the field has already been assigned, return its value.
                        builder.WriteLine($"if ({node.FieldName} != {Null}) {{ return {node.FieldName}; }}");
                    }

                    builder.WriteLine($"{ConstVar} result = {node.FieldName} = {createCallText};");
                }
                else
                {
                    builder.WriteLine($"{ConstVar} result = {createCallText};");
                }
            }

            void WriteHelperExpressionAnimationBinder(CodeBuilder builder)
            {
                // 1 reference parameter version.
                builder.WriteLine($"void BindProperty(");
                builder.Indent();
                builder.WriteLine($"{ReferenceTypeName("CompositionObject")} target,");
                builder.WriteLine($"{_stringifier.StringType} animatedPropertyName,");
                builder.WriteLine($"{_stringifier.StringType} expression,");
                builder.WriteLine($"{_stringifier.StringType} referenceParameterName,");
                builder.WriteLine($"{ReferenceTypeName("CompositionObject")} referencedObject)");
                builder.UnIndent();
                builder.OpenScope();
                builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}ClearAllParameters();");
                WritePropertySetStatement(builder, SingletonExpressionAnimationName, "Expression", "expression");
                builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}SetReferenceParameter(referenceParameterName, referencedObject);");
                builder.WriteLine($"target{Deref}StartAnimation(animatedPropertyName, {SingletonExpressionAnimationName});");
                builder.CloseScope();
                builder.WriteLine();

                // 2 reference parameter version.
                builder.WriteLine($"void BindProperty2(");
                builder.Indent();
                builder.WriteLine($"{ReferenceTypeName("CompositionObject")} target,");
                builder.WriteLine($"{_stringifier.StringType} animatedPropertyName,");
                builder.WriteLine($"{_stringifier.StringType} expression,");
                builder.WriteLine($"{_stringifier.StringType} referenceParameterName0,");
                builder.WriteLine($"{ReferenceTypeName("CompositionObject")} referencedObject0,");
                builder.WriteLine($"{_stringifier.StringType} referenceParameterName1,");
                builder.WriteLine($"{ReferenceTypeName("CompositionObject")} referencedObject1)");
                builder.UnIndent();
                builder.OpenScope();
                builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}ClearAllParameters();");
                WritePropertySetStatement(builder, SingletonExpressionAnimationName, "Expression", "expression");
                builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}SetReferenceParameter(referenceParameterName0, referencedObject0);");
                builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}SetReferenceParameter(referenceParameterName1, referencedObject1);");
                builder.WriteLine($"target{Deref}StartAnimation(animatedPropertyName, {SingletonExpressionAnimationName});");
                builder.CloseScope();
                builder.WriteLine();
            }

            void WriteBoolPropertySetStatement(CodeBuilder builder, string target, string propertyName, bool value)
                => WritePropertySetStatement(builder, target, propertyName, Bool(value));

            void WriteFloatPropertySetStatement(CodeBuilder builder, string target, string propertyName, float value)
                 => WritePropertySetStatement(builder, target, propertyName, Float(value));

            void WriteFloatPropertySetStatement(CodeBuilder builder, string target, string propertyName, float? value)
            {
                if (value.HasValue)
                {
                    WritePropertySetStatement(builder, target, propertyName, Float(value.Value));
                }
            }

            void WriteVector2PropertySetStatement(CodeBuilder builder, string target, string propertyName, Vector2 value)
                 => WritePropertySetStatement(builder, target, propertyName, Vector2(value));

            void WriteVector2PropertySetStatement(CodeBuilder builder, string target, string propertyName, Vector2? value)
            {
                if (value.HasValue)
                {
                    WritePropertySetStatement(builder, target, propertyName, Vector2(value.Value));
                }
            }

            void WriteVector3PropertySetStatement(CodeBuilder builder, string target, string propertyName, Vector3? value)
            {
                if (value.HasValue)
                {
                    WritePropertySetStatement(builder, target, propertyName, Vector3(value.Value));
                }
            }

            void WritePropertySetStatement(CodeBuilder builder, string target, string propertyName, string value)
            {
                builder.WriteLine($"{_stringifier.PropertySet(target, propertyName, value)};");
            }

            void WritePopulateShapesCollection(CodeBuilder builder, IList<CompositionShape> shapes, ObjectData node)
            {
                switch (shapes.Count)
                {
                    case 0:
                        // No items, nothing to do.
                        break;

                    case 1:
                        {
                            // A single item. We can add the shape in a single line.
                            var shape = shapes[0];
                            builder.WriteComment(((IDescribable)shape).ShortDescription);
                            builder.WriteLine($"{_stringifier.PropertyGet("result", "Shapes")}{Deref}{IListAdd}({CallFactoryFromFor(node, shape)});");
                            break;
                        }

                    default:
                        {
                            // Multiple items requires the use of a local.
                            builder.WriteLine($"{ConstVar} shapes = {_stringifier.PropertyGet("result", "Shapes")};");
                            foreach (var shape in shapes)
                            {
                                builder.WriteComment(((IDescribable)shape).ShortDescription);
                                builder.WriteLine($"shapes{Deref}{IListAdd}({CallFactoryFromFor(node, shape)});");
                            }

                            break;
                        }
                }
            }

            internal void WriteAnimatedVisualCode(CodeBuilder builder)
            {
                _owner._currentAnimatedVisualGenerator = this;

                // Write the body of the AnimatedVisual class.
                _owner.WriteAnimatedVisualStart(builder, this);

                // Write fields for constant values.
                builder.WriteComment($"Animation duration: {_owner._compositionDuration.Ticks / (double)System.TimeSpan.TicksPerSecond,-1:N3} seconds.");
                builder.WriteLine(ConstExprField(_stringifier.Int64TypeName, DurationTicksFieldName, $"{_stringifier.Int64(_owner._compositionDuration.Ticks)}"));

                // Write fields for each object that needs storage (i.e. objects that are referenced more than once).
                // Write read-only fields first.
                _owner.WriteDefaultInitializedField(builder, Readonly(_stringifier.ReferenceTypeName("Compositor")), "_c");
                _owner.WriteDefaultInitializedField(builder, Readonly(_stringifier.ReferenceTypeName("ExpressionAnimation")), SingletonExpressionAnimationName);

                if (_owner._isThemed)
                {
                    _owner.WriteDefaultInitializedField(builder, Readonly(_stringifier.ReferenceTypeName("CompositionPropertySet")), ThemePropertiesFieldName);
                }

                WriteFields(builder);

                builder.WriteLine();

                // Write the method that binds an expression to an object using the singleton ExpressionAnimation object.
                WriteHelperExpressionAnimationBinder(builder);

                // Write factory methods for each node.
                foreach (var node in _nodes)
                {
                    // Only generate a factory method if the node is not inlined into the caller.
                    if (!node.Inlined)
                    {
                        WriteFactoryForNode(builder, node);
                    }
                }

                // Write the end of the AnimatedVisual class.
                _owner.WriteAnimatedVisualEnd(builder, this);

                _owner._currentAnimatedVisualGenerator = null;
            }

            void WriteFields(CodeBuilder builder)
            {
                foreach (var node in OrderByName(_nodes.Where(n => n.RequiresReadonlyStorage)))
                {
                    // Generate a field for the read-only storage.
                    _owner.WriteDefaultInitializedField(builder, Readonly(_stringifier.ReferenceTypeName(node.TypeName)), node.FieldName);
                }

                foreach (var node in OrderByName(_nodes.Where(n => n.RequiresStorage && !n.RequiresReadonlyStorage)))
                {
                    // Generate a field for the non-read-only storage.
                    _owner.WriteDefaultInitializedField(builder, _stringifier.ReferenceTypeName(node.TypeName), node.FieldName);
                }
            }

            // Generates a factory method for the given node. The code is written into the given CodeBuilder.
            void WriteFactoryForNode(CodeBuilder builder, ObjectData node)
            {
                switch (node.Type)
                {
                    case Graph.NodeType.CompositionObject:
                        GenerateObjectFactory(builder, (CompositionObject)node.Object, node);
                        break;
                    case Graph.NodeType.CompositionPath:
                        GenerateCompositionPathFactory(builder, (CompositionPath)node.Object, node);
                        break;
                    case Graph.NodeType.CanvasGeometry:
                        GenerateCanvasGeometryFactory(builder, (CanvasGeometry)node.Object, node);
                        break;
                    case Graph.NodeType.LoadedImageSurface:
                        // LoadedImageSurface is written out in the IDynamicAnimatedVisualSource class, so does not need to do anything here.
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }

            string CallCreateCubicBezierEasingFunction(CubicBezierEasingFunction obj)
                => $"_c{Deref}CreateCubicBezierEasingFunction({Vector2(obj.ControlPoint1)}, {Vector2(obj.ControlPoint2)})";

            bool GenerateCanvasGeometryFactory(CodeBuilder builder, CanvasGeometry obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                var typeName = _stringifier.ReferenceTypeName(node.TypeName);
                var fieldName = node.FieldName;

                switch (obj.Type)
                {
                    case CanvasGeometry.GeometryType.Combination:
                        _owner.WriteCanvasGeometryCombinationFactory(builder, (CanvasGeometry.Combination)obj, typeName, fieldName);
                        break;
                    case CanvasGeometry.GeometryType.Ellipse:
                        _owner.WriteCanvasGeometryEllipseFactory(builder, (CanvasGeometry.Ellipse)obj, typeName, fieldName);
                        break;
                    case CanvasGeometry.GeometryType.Group:
                        _owner.WriteCanvasGeometryGroupFactory(builder, (CanvasGeometry.Group)obj, typeName, fieldName);
                        break;
                    case CanvasGeometry.GeometryType.Path:
                        _owner.WriteCanvasGeometryPathFactory(builder, (CanvasGeometry.Path)obj, typeName, fieldName);
                        break;
                    case CanvasGeometry.GeometryType.RoundedRectangle:
                        _owner.WriteCanvasGeometryRoundedRectangleFactory(builder, (CanvasGeometry.RoundedRectangle)obj, typeName, fieldName);
                        break;
                    case CanvasGeometry.GeometryType.TransformedGeometry:
                        _owner.WriteCanvasGeometryTransformedGeometryFactory(builder, (CanvasGeometry.TransformedGeometry)obj, typeName, fieldName);
                        break;
                    default:
                        throw new InvalidOperationException();
                }

                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateObjectFactory(CodeBuilder builder, CompositionObject obj, ObjectData node)
            {
                // Uncomment to see the order of creation.
                builder.WriteComment($"Traversal order: {node.Position}");
                switch (obj.Type)
                {
                    case CompositionObjectType.AnimationController:
                        // Do not generate code for animation controllers. It is done inline in the CompositionObject initialization.
                        throw new InvalidOperationException();
                    case CompositionObjectType.ColorKeyFrameAnimation:
                        return GenerateColorKeyFrameAnimationFactory(builder, (ColorKeyFrameAnimation)obj, node);
                    case CompositionObjectType.CompositionColorBrush:
                        return GenerateCompositionColorBrushFactory(builder, (CompositionColorBrush)obj, node);
                    case CompositionObjectType.CompositionColorGradientStop:
                        return GenerateCompositionColorGradientStopFactory(builder, (CompositionColorGradientStop)obj, node);
                    case CompositionObjectType.CompositionContainerShape:
                        return GenerateContainerShapeFactory(builder, (CompositionContainerShape)obj, node);
                    case CompositionObjectType.CompositionEffectBrush:
                        return GenerateCompositionEffectBrushFactory(builder, (CompositionEffectBrush)obj, node);
                    case CompositionObjectType.CompositionEllipseGeometry:
                        return GenerateCompositionEllipseGeometryFactory(builder, (CompositionEllipseGeometry)obj, node);
                    case CompositionObjectType.CompositionGeometricClip:
                        return GenerateCompositionGeometricClipFactory(builder, (CompositionGeometricClip)obj, node);
                    case CompositionObjectType.CompositionLinearGradientBrush:
                        return GenerateCompositionLinearGradientBrushFactory(builder, (CompositionLinearGradientBrush)obj, node);
                    case CompositionObjectType.CompositionPathGeometry:
                        return GenerateCompositionPathGeometryFactory(builder, (CompositionPathGeometry)obj, node);
                    case CompositionObjectType.CompositionPropertySet:
                        // Do not generate code for property sets. It is done inline in the CompositionObject initialization.
                        return true;
                    case CompositionObjectType.CompositionRadialGradientBrush:
                        return GenerateCompositionRadialGradientBrushFactory(builder, (CompositionRadialGradientBrush)obj, node);
                    case CompositionObjectType.CompositionRectangleGeometry:
                        return GenerateCompositionRectangleGeometryFactory(builder, (CompositionRectangleGeometry)obj, node);
                    case CompositionObjectType.CompositionRoundedRectangleGeometry:
                        return GenerateCompositionRoundedRectangleGeometryFactory(builder, (CompositionRoundedRectangleGeometry)obj, node);
                    case CompositionObjectType.CompositionSpriteShape:
                        return GenerateSpriteShapeFactory(builder, (CompositionSpriteShape)obj, node);
                    case CompositionObjectType.CompositionSurfaceBrush:
                        return GenerateCompositionSurfaceBrushFactory(builder, (CompositionSurfaceBrush)obj, node);
                    case CompositionObjectType.CompositionViewBox:
                        return GenerateCompositionViewBoxFactory(builder, (CompositionViewBox)obj, node);
                    case CompositionObjectType.CompositionVisualSurface:
                        return GenerateCompositionVisualSurfaceFactory(builder, (CompositionVisualSurface)obj, node);
                    case CompositionObjectType.ContainerVisual:
                        return GenerateContainerVisualFactory(builder, (ContainerVisual)obj, node);
                    case CompositionObjectType.CubicBezierEasingFunction:
                        return GenerateCubicBezierEasingFunctionFactory(builder, (CubicBezierEasingFunction)obj, node);
                    case CompositionObjectType.ExpressionAnimation:
                        return GenerateExpressionAnimationFactory(builder, (ExpressionAnimation)obj, node);
                    case CompositionObjectType.InsetClip:
                        return GenerateInsetClipFactory(builder, (InsetClip)obj, node);
                    case CompositionObjectType.LinearEasingFunction:
                        return GenerateLinearEasingFunctionFactory(builder, (LinearEasingFunction)obj, node);
                    case CompositionObjectType.PathKeyFrameAnimation:
                        return GeneratePathKeyFrameAnimationFactory(builder, (PathKeyFrameAnimation)obj, node);
                    case CompositionObjectType.ScalarKeyFrameAnimation:
                        return GenerateScalarKeyFrameAnimationFactory(builder, (ScalarKeyFrameAnimation)obj, node);
                    case CompositionObjectType.ShapeVisual:
                        return GenerateShapeVisualFactory(builder, (ShapeVisual)obj, node);
                    case CompositionObjectType.SpriteVisual:
                        return GenerateSpriteVisualFactory(builder, (SpriteVisual)obj, node);
                    case CompositionObjectType.StepEasingFunction:
                        return GenerateStepEasingFunctionFactory(builder, (StepEasingFunction)obj, node);
                    case CompositionObjectType.Vector2KeyFrameAnimation:
                        return GenerateVector2KeyFrameAnimationFactory(builder, (Vector2KeyFrameAnimation)obj, node);
                    case CompositionObjectType.Vector3KeyFrameAnimation:
                        return GenerateVector3KeyFrameAnimationFactory(builder, (Vector3KeyFrameAnimation)obj, node);
                    case CompositionObjectType.Vector4KeyFrameAnimation:
                        return GenerateVector4KeyFrameAnimationFactory(builder, (Vector4KeyFrameAnimation)obj, node);
                    default:
                        throw new InvalidOperationException();
                }
            }

            bool GenerateInsetClipFactory(CodeBuilder builder, InsetClip obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateInsetClip()");
                InitializeCompositionClip(builder, obj, node);

                if (obj.LeftInset != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "LeftInset", obj.LeftInset);
                }

                if (obj.RightInset != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "RightInset", obj.RightInset);
                }

                if (obj.TopInset != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "TopInset", obj.TopInset);
                }

                if (obj.BottomInset != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "BottomInset", obj.BottomInset);
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionGeometricClipFactory(CodeBuilder builder, CompositionGeometricClip obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateGeometricClip()");
                InitializeCompositionClip(builder, obj, node);

                if (obj.Geometry != null)
                {
                    WritePropertySetStatement(builder, "result", "Geometry", CallFactoryFromFor(node, obj.Geometry));
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionLinearGradientBrushFactory(CodeBuilder builder, CompositionLinearGradientBrush obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateLinearGradientBrush()");
                InitializeCompositionGradientBrush(builder, obj, node);

                WriteVector2PropertySetStatement(builder, "result", "StartPoint", obj.StartPoint);

                if (obj.EndPoint.HasValue)
                {
                    WriteVector2PropertySetStatement(builder, "result", "EndPoint", obj.EndPoint);
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionRadialGradientBrushFactory(CodeBuilder builder, CompositionRadialGradientBrush obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateRadialGradientBrush()");
                InitializeCompositionGradientBrush(builder, obj, node);

                WriteVector2PropertySetStatement(builder, "result", "EllipseCenter", obj.EllipseCenter);
                WriteVector2PropertySetStatement(builder, "result", "EllipseRadius", obj.EllipseRadius);
                WriteVector2PropertySetStatement(builder, "result", "GradientOriginOffset", obj.GradientOriginOffset);

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateLinearEasingFunctionFactory(CodeBuilder builder, LinearEasingFunction obj, ObjectData node)
            {
                WriteSimpleObjectFactory(builder, node, $"_c{Deref}CreateLinearEasingFunction()");
                return true;
            }

            bool GenerateCubicBezierEasingFunctionFactory(CodeBuilder builder, CubicBezierEasingFunction obj, ObjectData node)
            {
                WriteSimpleObjectFactory(builder, node, CallCreateCubicBezierEasingFunction(obj));
                return true;
            }

            bool GenerateStepEasingFunctionFactory(CodeBuilder builder, StepEasingFunction obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateStepEasingFunction()");

                if (obj.FinalStep != 1)
                {
                    WritePropertySetStatement(builder, "result", "FinalStep", Int(obj.FinalStep));
                }

                if (obj.InitialStep != 0)
                {
                    WritePropertySetStatement(builder, "result", "InitialStep", Int(obj.InitialStep));
                }

                if (obj.IsFinalStepSingleFrame)
                {
                    WriteBoolPropertySetStatement(builder, "result", "IsFinalStepSingleFrame", obj.IsFinalStepSingleFrame);
                }

                if (obj.IsInitialStepSingleFrame)
                {
                    WriteBoolPropertySetStatement(builder, "result", "IsInitialStepSingleFrame", obj.IsInitialStepSingleFrame);
                }

                if (obj.StepCount != 1)
                {
                    WritePropertySetStatement(builder, "result", "StepCount", Int(obj.StepCount));
                }

                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateContainerVisualFactory(CodeBuilder builder, ContainerVisual obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateContainerVisual()");
                InitializeContainerVisual(builder, obj, node);
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateExpressionAnimationFactory(CodeBuilder builder, ExpressionAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateExpressionAnimation({String(obj.Expression)})");
                InitializeCompositionAnimation(builder, obj, node);
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            void StartAnimationsOnResult(CodeBuilder builder, CompositionObject obj, ObjectData node)
                => StartAnimations(builder, obj, node, "result");

            void StartAnimations(CodeBuilder builder, CompositionObject obj, ObjectData node, string localName)
            {
                var controllerVariableAdded = false;
                StartAnimations(builder, obj, node, localName, ref controllerVariableAdded);
            }

            void StartAnimations(CodeBuilder builder, CompositionObject obj, ObjectData node, string localName, ref bool controllerVariableAdded)
            {
                // Start the animations for properties on the object.
                foreach (var animator in obj.Animators)
                {
                    StartAnimation(builder, obj, node, localName, ref controllerVariableAdded, animator);
                }

                // Start the animations for the properties on the property set on the object.
                // Prevent infinite recursion - the Properties on a CompositionPropertySet is itself.
                if (obj.Type != CompositionObjectType.CompositionPropertySet)
                {
                    // Start the animations for properties on the property set.
                    StartAnimations(builder, obj.Properties, NodeFor(obj.Properties), "propertySet", ref controllerVariableAdded);
                }
            }

            void StartAnimation(CodeBuilder builder, CompositionObject obj, ObjectData node, string localName, ref bool controllerVariableAdded, CompositionObject.Animator animator)
            {
                // ExpressionAnimations are treated specially - a singleton
                // ExpressionAnimation is reset before each use, unless the animation
                // is shared.
                var animationNode = NodeFor(animator.Animation);
                if (!animationNode.RequiresStorage && animator.Animation is ExpressionAnimation expressionAnimation)
                {
                    StartSingletonExpressionAnimation(builder, obj, localName, animator, animationNode, expressionAnimation);
                    ConfigureAnimationController(builder, localName, ref controllerVariableAdded, animator);
                }
                else
                {
                    // KeyFrameAnimation or a shared ExpressionAnimation
                    var animationFactoryCall = CallFactoryFromFor(node, animationNode);

                    if (animator.Controller != null)
                    {
                        // The animation has a controller.
                        var controller = animator.Controller;

                        var controllerAnimators = controller.Animators;

                        if (controllerAnimators.Count == 1)
                        {
                            // The controller has only one property being animated.
                            var controllerAnimator = controllerAnimators[0];
                            if (controllerAnimator.AnimatedProperty == "Progress" &&
                                controllerAnimator.Animation is ExpressionAnimation controllerExpressionAnimation &&
                                controller.IsPaused)
                            {
                                // The controller has only its Progress property animated, and it's animated by
                                // an expression animation.
                                var controllerExpressionAnimationNode = NodeFor(controllerExpressionAnimation);

                                if (controllerExpressionAnimationNode.NeedsAFactory)
                                {
                                    // Special-case for a paused controller that has only its Progress property animated by
                                    // an ExpressionAnimation that has a factory. Generate a call to a helper that will do the work.
                                    // Note that this is the common case for Lottie.
                                    builder.WriteLine(
                                        $"StartProgressBoundAnimation({localName}, " +
                                        $"{String(animator.AnimatedProperty)}, " +
                                        $"{animationFactoryCall}, " +
                                        $"{CallFactoryFromFor(NodeFor(animator.Controller), controllerExpressionAnimationNode)});");
                                    return;
                                }
                            }
                        }
                    }

                    builder.WriteLine($"{localName}{Deref}StartAnimation({String(animator.AnimatedProperty)}, {animationFactoryCall});");
                    ConfigureAnimationController(builder, localName, ref controllerVariableAdded, animator);
                }
            }

            void ConfigureAnimationController(CodeBuilder builder, string localName, ref bool controllerVariableAdded, CompositionObject.Animator animator)
            {
                // If the animation has a controller, get the controller, optionally pause it, and recurse to start the animations
                // on the controller.
                if (animator.Controller != null)
                {
                    var controller = animator.Controller;

                    if (!controllerVariableAdded)
                    {
                        // Declare and initialize the controller variable.
                        builder.WriteLine($"{ConstVar} controller = {localName}{Deref}TryGetAnimationController({String(animator.AnimatedProperty)});");
                        controllerVariableAdded = true;
                    }
                    else
                    {
                        // Initialize the controller variable.
                        builder.WriteLine($"controller = {localName}{Deref}TryGetAnimationController({String(animator.AnimatedProperty)});");
                    }

                    if (controller.IsPaused)
                    {
                        builder.WriteLine($"controller{Deref}Pause();");
                    }

                    // Recurse to start animations on the controller.
                    StartAnimations(builder, controller, NodeFor(controller), "controller");
                }
            }

            // Starts an ExpressionAnimation that uses the shared singleton ExpressionAnimation.
            // This reparameterizes the singleton each time it is called, and therefore avoids the
            // cost of creating a new ExpressionAnimation. However, because it gets reparameterized
            // for each use, it cannot be used if the ExpressionAnimation is shared by multiple nodes.
            void StartSingletonExpressionAnimation(
                    CodeBuilder builder,
                    CompositionObject obj,
                    string localName,
                    CompositionObject.Animator animator,
                    ObjectData animationNode,
                    ExpressionAnimation animation)
            {
                Debug.Assert(animator.Animation == animation, "Precondition");

                var referenceParameters = animator.Animation.ReferenceParameters.ToArray();
                if (referenceParameters.Length == 1 &&
                    string.IsNullOrWhiteSpace(animation.Target))
                {
                    var rp0 = referenceParameters[0];
                    var rp0Name = GetReferenceParameterName(obj, localName, animationNode, rp0);

                    // Special-case where there is exactly one reference parameter. Call a helper.
                    builder.WriteLine(
                        $"BindProperty({localName}, " + // target
                        $"{String(animator.AnimatedProperty)}, " + // property on target
                        $"{String(animation.Expression.ToText())}, " + // expression
                        $"{String(rp0.Key)}, " + // reference property name
                        $"{rp0Name});"); // reference object
                }
                else if (referenceParameters.Length == 2 &&
                    string.IsNullOrWhiteSpace(animation.Target))
                {
                    var rp0 = referenceParameters[0];
                    var rp0Name = GetReferenceParameterName(obj, localName, animationNode, rp0);

                    var rp1 = referenceParameters[1];
                    var rp1Name = GetReferenceParameterName(obj, localName, animationNode, rp1);

                    // Special-case where there are exactly two reference parameters. Call a helper.
                    builder.WriteLine(
                        $"BindProperty2({localName}, " + // target
                        $"{String(animator.AnimatedProperty)}, " + // property on target
                        $"{String(animation.Expression.ToText())}, " + // expression
                        $"{String(rp0.Key)}, " + // reference property name
                        $"{rp0Name}, " + // reference object
                        $"{String(rp1.Key)}, " + // reference property name
                        $"{rp1Name});"); // reference object
                }
                else
                {
                    builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}ClearAllParameters();");
                    builder.WriteLine($"{_stringifier.PropertySet(SingletonExpressionAnimationName, "Expression", String(animation.Expression))};");

                    // If there is a Target set it. Note however that the Target isn't used for anything
                    // interesting in this scenario, and there is no way to reset the Target to an
                    // empty string (the Target API disallows empty). In reality, for all our uses
                    // the Target will not be set and it doesn't matter if it was set previously.
                    if (!string.IsNullOrWhiteSpace(animation.Target))
                    {
                        builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}Target = {String(animation.Target)};");
                    }

                    foreach (var rp in animation.ReferenceParameters)
                    {
                        var referenceParameterName = GetReferenceParameterName(obj, localName, animationNode, rp);

                        builder.WriteLine($"{SingletonExpressionAnimationName}{Deref}SetReferenceParameter({String(rp.Key)}, {referenceParameterName});");
                    }

                    builder.WriteLine($"{localName}{Deref}StartAnimation({String(animator.AnimatedProperty)}, {SingletonExpressionAnimationName});");
                }
            }

            string GetReferenceParameterName(
                CompositionObject obj,
                string localName,
                ObjectData animationNode,
                KeyValuePair<string, CompositionObject> referenceParameter)
            {
                if (referenceParameter.Value == obj)
                {
                    return localName;
                }

                if (referenceParameter.Value.Type == CompositionObjectType.CompositionPropertySet)
                {
                    var propSet = (CompositionPropertySet)referenceParameter.Value;
                    var propSetOwner = propSet.Owner;
                    if (propSetOwner == obj)
                    {
                        // Use the name of the local that is holding the property set.
                        return "propertySet";
                    }

                    if (propSetOwner is null)
                    {
                        // It's an unowned property set. Currently these are:
                        // * only used for themes.
                        // * placed in a field by the constructor of the IAnimatedVisual.
                        Debug.Assert(_owner._isThemed, "Precondition");
                        return ThemePropertiesFieldName;
                    }

                    // Get the factory for the owner of the property set, and get the Properties object from it.
                    return CallFactoryFromFor(animationNode, propSetOwner);
                }

                return CallFactoryFromFor(animationNode, referenceParameter.Value);
            }

            void InitializeCompositionObject(CodeBuilder builder, CompositionObject obj, ObjectData node, string localName = "result")
            {
                if (_owner._setCommentProperties)
                {
                    if (!string.IsNullOrWhiteSpace(obj.Comment))
                    {
                        WritePropertySetStatement(builder, localName, "Comment", String(obj.Comment));
                    }
                }

                var propertySet = obj.Properties;

                if (propertySet.Names.Count > 0)
                {
                    builder.WriteLine($"{ConstVar} propertySet = {_stringifier.PropertyGet(localName, "Properties")};");
                    _owner.WritePropertySetInitialization(builder, propertySet, "propertySet");
                }
            }

            void InitializeCompositionBrush(CodeBuilder builder, CompositionBrush obj, ObjectData node)
            {
                InitializeCompositionObject(builder, obj, node);
            }

            void InitializeVisual(CodeBuilder builder, Visual obj, ObjectData node)
            {
                InitializeCompositionObject(builder, obj, node);

                if (obj.BorderMode.HasValue)
                {
                    WritePropertySetStatement(builder, "result", "BorderMode", BorderMode(obj.BorderMode.Value));
                }

                WriteVector3PropertySetStatement(builder, "result", "CenterPoint", obj.CenterPoint);

                if (obj.Clip != null)
                {
                    WritePropertySetStatement(builder, "result", "Clip", CallFactoryFromFor(node, obj.Clip));
                }

                WriteVector3PropertySetStatement(builder, "result", "Offset", obj.Offset);
                WriteFloatPropertySetStatement(builder, "result", "Opacity", obj.Opacity);
                WriteFloatPropertySetStatement(builder, "result", "RotationAngleInDegrees", obj.RotationAngleInDegrees);
                WriteVector3PropertySetStatement(builder, "result", "RotationAxis", obj.RotationAxis);
                WriteVector3PropertySetStatement(builder, "result", "Scale", obj.Scale);
                WriteVector2PropertySetStatement(builder, "result", "Size", obj.Size);

                if (obj.TransformMatrix.HasValue)
                {
                    WritePropertySetStatement(builder, "result", "TransformMatrix", Matrix4x4(obj.TransformMatrix.Value));
                }
            }

            void InitializeCompositionClip(CodeBuilder builder, CompositionClip obj, ObjectData node)
            {
                InitializeCompositionObject(builder, obj, node);

                if (obj.CenterPoint.X != 0 || obj.CenterPoint.Y != 0)
                {
                    WriteVector2PropertySetStatement(builder, "result", "CenterPoint", obj.CenterPoint);
                }

                if (obj.Scale.X != 1 || obj.Scale.Y != 1)
                {
                    WriteVector2PropertySetStatement(builder, "result", "Scale", obj.Scale);
                }
            }

            void InitializeCompositionGradientBrush(CodeBuilder builder, CompositionGradientBrush obj, ObjectData node)
            {
                InitializeCompositionObject(builder, obj, node);

                WriteVector2PropertySetStatement(builder, "result", "AnchorPoint", obj.AnchorPoint);
                WriteVector2PropertySetStatement(builder, "result", "CenterPoint", obj.CenterPoint);

                if (obj.ColorStops.Count > 0)
                {
                    builder.WriteLine($"{ConstVar} colorStops = {_stringifier.PropertyGet("result", "ColorStops")};");
                    foreach (var colorStop in obj.ColorStops)
                    {
                        builder.WriteLine($"colorStops{Deref}{IListAdd}({CallFactoryFromFor(node, colorStop)});");
                    }
                }

                if (obj.ExtendMode.HasValue)
                {
                    WritePropertySetStatement(builder, "result", "ExtendMode", ExtendMode(obj.ExtendMode.Value));
                }

                if (obj.InterpolationSpace.HasValue)
                {
                    WritePropertySetStatement(builder, "result", "InterpolationSpace", ColorSpace(obj.InterpolationSpace.Value));
                }

                // Default MappingMode is Relative
                if (obj.MappingMode.HasValue && obj.MappingMode.Value != CompositionMappingMode.Relative)
                {
                    WritePropertySetStatement(builder, "result", "MappingMode", MappingMode(obj.MappingMode.Value));
                }

                WriteVector2PropertySetStatement(builder, "result", "Offset", obj.Offset);
                WriteFloatPropertySetStatement(builder, "result", "RotationAngleInDegrees", obj.RotationAngleInDegrees);
                WriteVector2PropertySetStatement(builder, "result", "Scale", obj.Scale);

                if (obj.TransformMatrix.HasValue)
                {
                    WritePropertySetStatement(builder, "result", "TransformMatrix ", Matrix3x2(obj.TransformMatrix.Value));
                }
            }

            void InitializeCompositionShape(CodeBuilder builder, CompositionShape obj, ObjectData node)
            {
                InitializeCompositionObject(builder, obj, node);

                WriteVector2PropertySetStatement(builder, "result", "CenterPoint", obj.CenterPoint);
                WriteVector2PropertySetStatement(builder, "result", "Offset", obj.Offset);
                WriteFloatPropertySetStatement(builder, "result", "RotationAngleInDegrees", obj.RotationAngleInDegrees);
                WriteVector2PropertySetStatement(builder, "result", "Scale", obj.Scale);

                if (obj.TransformMatrix.HasValue)
                {
                    WritePropertySetStatement(builder, "result", "TransformMatrix", Matrix3x2(obj.TransformMatrix.Value));
                }
            }

            void InitializeContainerVisual(CodeBuilder builder, ContainerVisual obj, ObjectData node)
            {
                InitializeVisual(builder, obj, node);

                switch (obj.Children.Count)
                {
                    case 0:
                        // No children, nothing to do.
                        break;

                    case 1:
                        {
                            // A single child. We can add the child in a single line.
                            var child = obj.Children[0];
                            builder.WriteComment(((IDescribable)child).ShortDescription);
                            builder.WriteLine($"{_stringifier.PropertyGet("result", "Children")}{Deref}InsertAtTop({CallFactoryFromFor(node, child)});");
                            break;
                        }

                    default:
                        {
                            // Multiple children requires the use of a local.
                            builder.WriteLine($"{ConstVar} children = {_stringifier.PropertyGet("result", "Children")};");
                            foreach (var child in obj.Children)
                            {
                                builder.WriteComment(((IDescribable)child).ShortDescription);
                                builder.WriteLine($"children{Deref}InsertAtTop({CallFactoryFromFor(node, child)});");
                            }

                            break;
                        }
                }
            }

            void InitializeCompositionGeometry(CodeBuilder builder, CompositionGeometry obj, ObjectData node)
            {
                InitializeCompositionObject(builder, obj, node);

                if (obj.TrimEnd != 1)
                {
                    WriteFloatPropertySetStatement(builder, "result", "TrimEnd", obj.TrimEnd);
                }

                if (obj.TrimOffset != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "TrimOffset", obj.TrimOffset);
                }

                if (obj.TrimStart != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "TrimStart", obj.TrimStart);
                }
            }

            void InitializeCompositionAnimation(CodeBuilder builder, CompositionAnimation obj, ObjectData node)
            {
                InitializeCompositionAnimationWithParameters(
                    builder,
                    obj,
                    node,
                    obj.ReferenceParameters.Select(p => new KeyValuePair<string, string>(p.Key, $"{CallFactoryFromFor(node, p.Value)}")));
            }

            void InitializeCompositionAnimationWithParameters(CodeBuilder builder, CompositionAnimation obj, ObjectData node, IEnumerable<KeyValuePair<string, string>> parameters)
            {
                InitializeCompositionObject(builder, obj, node);
                if (!string.IsNullOrWhiteSpace(obj.Target))
                {
                    WritePropertySetStatement(builder, "result", "Target", String(obj.Target));
                }

                foreach (var parameter in parameters)
                {
                    builder.WriteLine($"result{Deref}SetReferenceParameter({String(parameter.Key)}, {parameter.Value});");
                }
            }

            void InitializeKeyFrameAnimation(CodeBuilder builder, KeyFrameAnimation_ obj, ObjectData node)
            {
                InitializeCompositionAnimation(builder, obj, node);
                WritePropertySetStatement(builder, "result", "Duration", TimeSpan(obj.Duration));
            }

            bool GenerateColorKeyFrameAnimationFactory(CodeBuilder builder, ColorKeyFrameAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateColorKeyFrameAnimation()");
                InitializeKeyFrameAnimation(builder, obj, node);

                if (obj.InterpolationColorSpace != CompositionColorSpace.Auto)
                {
                    WritePropertySetStatement(builder, "result", "InterpolationColorSpace", ColorSpace(obj.InterpolationColorSpace));
                }

                foreach (var kf in obj.KeyFrames)
                {
                    switch (kf.Type)
                    {
                        case KeyFrameType.Expression:
                            var expressionKeyFrame = (KeyFrameAnimation<Wui.Color, Expr.Color>.ExpressionKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        case KeyFrameType.Value:
                            var valueKeyFrame = (KeyFrameAnimation<Wui.Color, Expr.Color>.ValueKeyFrame)kf;
                            builder.WriteComment(valueKeyFrame.Value.Name);
                            builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Color(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateVector2KeyFrameAnimationFactory(CodeBuilder builder, Vector2KeyFrameAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateVector2KeyFrameAnimation()");
                InitializeKeyFrameAnimation(builder, obj, node);

                foreach (var kf in obj.KeyFrames)
                {
                    switch (kf.Type)
                    {
                        case KeyFrameType.Expression:
                            var expressionKeyFrame = (KeyFrameAnimation<Vector2, Expr.Vector2>.ExpressionKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        case KeyFrameType.Value:
                            var valueKeyFrame = (KeyFrameAnimation<Vector2, Expr.Vector2>.ValueKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Vector2(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateVector3KeyFrameAnimationFactory(CodeBuilder builder, Vector3KeyFrameAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateVector3KeyFrameAnimation()");
                InitializeKeyFrameAnimation(builder, obj, node);

                foreach (var kf in obj.KeyFrames)
                {
                    switch (kf.Type)
                    {
                        case KeyFrameType.Expression:
                            var expressionKeyFrame = (KeyFrameAnimation<Vector3, Expr.Vector3>.ExpressionKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        case KeyFrameType.Value:
                            var valueKeyFrame = (KeyFrameAnimation<Vector3, Expr.Vector3>.ValueKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Vector3(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateVector4KeyFrameAnimationFactory(CodeBuilder builder, Vector4KeyFrameAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateVector4KeyFrameAnimation()");
                InitializeKeyFrameAnimation(builder, obj, node);

                foreach (var kf in obj.KeyFrames)
                {
                    switch (kf.Type)
                    {
                        case KeyFrameType.Expression:
                            var expressionKeyFrame = (KeyFrameAnimation<Sn.Vector4, Expr.Vector4>.ExpressionKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        case KeyFrameType.Value:
                            var valueKeyFrame = (KeyFrameAnimation<Sn.Vector4, Expr.Vector4>.ValueKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Vector4(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GeneratePathKeyFrameAnimationFactory(CodeBuilder builder, PathKeyFrameAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreatePathKeyFrameAnimation()");
                InitializeKeyFrameAnimation(builder, obj, node);

                foreach (var kf in obj.KeyFrames)
                {
                    var valueKeyFrame = (PathKeyFrameAnimation.ValueKeyFrame)kf;
                    builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {CallFactoryFromFor(node, valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateScalarKeyFrameAnimationFactory(CodeBuilder builder, ScalarKeyFrameAnimation obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateScalarKeyFrameAnimation()");
                InitializeKeyFrameAnimation(builder, obj, node);

                foreach (var kf in obj.KeyFrames)
                {
                    switch (kf.Type)
                    {
                        case KeyFrameType.Expression:
                            var expressionKeyFrame = (KeyFrameAnimation<float, Expr.Scalar>.ExpressionKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertExpressionKeyFrame({Float(kf.Progress)}, {String(expressionKeyFrame.Expression)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        case KeyFrameType.Value:
                            var valueKeyFrame = (KeyFrameAnimation<float, Expr.Scalar>.ValueKeyFrame)kf;
                            builder.WriteLine($"result{Deref}InsertKeyFrame({Float(kf.Progress)}, {Float(valueKeyFrame.Value)}, {CallFactoryFromFor(node, kf.Easing)});");
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionRectangleGeometryFactory(CodeBuilder builder, CompositionRectangleGeometry obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateRectangleGeometry()");
                InitializeCompositionGeometry(builder, obj, node);

                WriteVector2PropertySetStatement(builder, "result", "Offset", obj.Offset);
                WriteVector2PropertySetStatement(builder, "result", "Size", obj.Size);

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionRoundedRectangleGeometryFactory(CodeBuilder builder, CompositionRoundedRectangleGeometry obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateRoundedRectangleGeometry()");
                InitializeCompositionGeometry(builder, obj, node);

                WriteVector2PropertySetStatement(builder, "result", "CornerRadius", obj.CornerRadius);
                WriteVector2PropertySetStatement(builder, "result", "Offset", obj.Offset);
                WriteVector2PropertySetStatement(builder, "result", "Size", obj.Size);

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionEllipseGeometryFactory(CodeBuilder builder, CompositionEllipseGeometry obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateEllipseGeometry()");
                InitializeCompositionGeometry(builder, obj, node);

                if (obj.Center.X != 0 || obj.Center.Y != 0)
                {
                    WriteVector2PropertySetStatement(builder, "result", "Center", obj.Center);
                }

                WriteVector2PropertySetStatement(builder, "result", "Radius", obj.Radius);
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionPathGeometryFactory(CodeBuilder builder, CompositionPathGeometry obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                if (obj.Path == null)
                {
                    WriteCreateAssignment(builder, node, $"_c{Deref}CreatePathGeometry()");
                }
                else
                {
                    var path = _objectGraph[obj.Path];
                    WriteCreateAssignment(builder, node, $"_c{Deref}CreatePathGeometry({CallFactoryFromFor(node, path)})");
                }

                InitializeCompositionGeometry(builder, obj, node);
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionColorBrushFactory(CodeBuilder builder, CompositionColorBrush obj, ObjectData node)
            {
                var createCallText = obj.Color.HasValue
                                        ? $"_c{Deref}CreateColorBrush({Color(obj.Color.Value)})"
                                        : $"_c{Deref}CreateColorBrush()";

                if (obj.Animators.Count > 0)
                {
                    WriteObjectFactoryStart(builder, node);
                    WriteCreateAssignment(builder, node, createCallText);
                    InitializeCompositionBrush(builder, obj, node);
                    StartAnimationsOnResult(builder, obj, node);
                    WriteObjectFactoryEnd(builder);
                }
                else
                {
                    WriteSimpleObjectFactory(builder, node, createCallText);
                }

                return true;
            }

            bool GenerateCompositionColorGradientStopFactory(CodeBuilder builder, CompositionColorGradientStop obj, ObjectData node)
            {
                if (obj.Animators.Count > 0)
                {
                    WriteObjectFactoryStart(builder, node);
                    WriteCreateAssignment(builder, node, $"_c{Deref}CreateColorGradientStop({Float(obj.Offset)}, {Color(obj.Color)})");
                    InitializeCompositionObject(builder, obj, node);
                    StartAnimationsOnResult(builder, obj, node);
                    WriteObjectFactoryEnd(builder);
                }
                else
                {
                    WriteSimpleObjectFactory(builder, node, $"_c{Deref}CreateColorGradientStop({Float(obj.Offset)}, {Color(obj.Color)})");
                }

                return true;
            }

            bool GenerateShapeVisualFactory(CodeBuilder builder, ShapeVisual obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateShapeVisual()");
                InitializeContainerVisual(builder, obj, node);
                WritePopulateShapesCollection(builder, obj.Shapes, node);
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateSpriteVisualFactory(CodeBuilder builder, SpriteVisual obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateSpriteVisual()");
                InitializeContainerVisual(builder, obj, node);

                if (obj.Brush != null)
                {
                    WritePropertySetStatement(builder, "result", "Brush", CallFactoryFromFor(node, obj.Brush));
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateContainerShapeFactory(CodeBuilder builder, CompositionContainerShape obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateContainerShape()");
                InitializeCompositionShape(builder, obj, node);
                WritePopulateShapesCollection(builder, obj.Shapes, node);
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionEffectBrushFactory(CodeBuilder builder, CompositionEffectBrush obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);

                var effect = obj.GetEffect();

                string effectCreationString;
                switch (effect.Type)
                {
                    case Mgce.GraphicsEffectType.CompositeEffect:
                        effectCreationString = _owner.WriteCompositeEffectFactory(builder, (Mgce.CompositeEffect)effect);
                        break;
                    default:
                        // Unsupported GraphicsEffectType.
                        throw new InvalidOperationException();
                }

                builder.WriteLine($"{ConstVar} effectFactory = _c{Deref}CreateEffectFactory({effectCreationString});");
                WriteCreateAssignment(builder, node, $"effectFactory{Deref}CreateBrush()");
                InitializeCompositionBrush(builder, obj, node);

                // Perform brush initialization
                switch (effect.Type)
                {
                    case Mgce.GraphicsEffectType.CompositeEffect:
                        foreach (var sourceParameters in ((Mgce.CompositeEffect)effect).Sources)
                        {
                            builder.WriteLine($"result{Deref}SetSourceParameter({String(sourceParameters.Name)}, {CallFactoryFromFor(node, obj.GetSourceParameter(sourceParameters.Name))});");
                        }

                        break;
                    default:
                        throw new InvalidOperationException();
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateSpriteShapeFactory(CodeBuilder builder, CompositionSpriteShape obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                if (obj.Geometry is null)
                {
                    WriteCreateAssignment(builder, node, $"_c{Deref}CreateSpriteShape()");
                }
                else
                {
                    WriteCreateAssignment(builder, node, $"_c{Deref}CreateSpriteShape({CallFactoryFromFor(node, obj.Geometry)})");
                }

                InitializeCompositionShape(builder, obj, node);

                if (obj.FillBrush != null)
                {
                    WritePropertySetStatement(builder, "result", "FillBrush", CallFactoryFromFor(node, obj.FillBrush));
                }

                if (obj.IsStrokeNonScaling)
                {
                    WriteBoolPropertySetStatement(builder, "result", "IsStrokeNonScaling", true);
                }

                if (obj.StrokeBrush != null)
                {
                    WritePropertySetStatement(builder, "result", "StrokeBrush", CallFactoryFromFor(node, obj.StrokeBrush));
                }

                if (obj.StrokeDashCap != CompositionStrokeCap.Flat)
                {
                    WritePropertySetStatement(builder, "result", "StrokeDashCap", StrokeCap(obj.StrokeDashCap));
                }

                if (obj.StrokeDashOffset != 0)
                {
                    WriteFloatPropertySetStatement(builder, "result", "StrokeDashOffset", obj.StrokeDashOffset);
                }

                if (obj.StrokeDashArray.Count > 0)
                {
                    builder.WriteLine($"{ConstVar} strokeDashArray = {_stringifier.PropertyGet("result", "StrokeDashArray")};");
                    foreach (var strokeDash in obj.StrokeDashArray)
                    {
                        builder.WriteLine($"strokeDashArray{Deref}{IListAdd}({Float(strokeDash)});");
                    }
                }

                if (obj.StrokeEndCap != CompositionStrokeCap.Flat)
                {
                    WritePropertySetStatement(builder, "result", "StrokeEndCap", StrokeCap(obj.StrokeEndCap));
                }

                if (obj.StrokeLineJoin != CompositionStrokeLineJoin.Miter)
                {
                    WritePropertySetStatement(builder, "result", "StrokeLineJoin", StrokeLineJoin(obj.StrokeLineJoin));
                }

                if (obj.StrokeStartCap != CompositionStrokeCap.Flat)
                {
                    WritePropertySetStatement(builder, "result", "StrokeStartCap", StrokeCap(obj.StrokeStartCap));
                }

                if (obj.StrokeMiterLimit != 1)
                {
                    WriteFloatPropertySetStatement(builder, "result", "StrokeMiterLimit", obj.StrokeMiterLimit);
                }

                if (obj.StrokeThickness != 1)
                {
                    WriteFloatPropertySetStatement(builder, "result", "StrokeThickness", obj.StrokeThickness);
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionSurfaceBrushFactory(CodeBuilder builder, CompositionSurfaceBrush obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateSurfaceBrush()");
                InitializeCompositionBrush(builder, obj, node);

                if (obj.Surface != null)
                {
                    switch (obj.Surface)
                    {
                        case CompositionObject compositionObject:
                            WritePropertySetStatement(builder, "result", "Surface", CallFactoryFromFor(node, compositionObject));
                            break;
                        case Wmd.LoadedImageSurface loadedImageSurface:
                            WritePropertySetStatement(builder, "result", "Surface", NodeFor(loadedImageSurface).FieldName);
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionViewBoxFactory(CodeBuilder builder, CompositionViewBox obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateViewBox()");
                InitializeCompositionObject(builder, obj, node);
                builder.WriteLine($"result.Size = {Vector2(obj.Size)};");
                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            bool GenerateCompositionVisualSurfaceFactory(CodeBuilder builder, CompositionVisualSurface obj, ObjectData node)
            {
                WriteObjectFactoryStart(builder, node);
                WriteCreateAssignment(builder, node, $"_c{Deref}CreateVisualSurface()");
                InitializeCompositionObject(builder, obj, node);

                if (obj.SourceVisual != null)
                {
                    WritePropertySetStatement(builder, "result", "SourceVisual", CallFactoryFromFor(node, obj.SourceVisual));
                }

                WriteVector2PropertySetStatement(builder, "result", "SourceSize", obj.SourceSize);
                WriteVector2PropertySetStatement(builder, "result", "SourceOffset", obj.SourceOffset);

                StartAnimationsOnResult(builder, obj, node);
                WriteObjectFactoryEnd(builder);
                return true;
            }

            IAnimatedVisualSourceInfo IAnimatedVisualInfo.AnimatedVisualSourceInfo => _owner;

            string IAnimatedVisualInfo.ClassName => "AnimatedVisual" + (_isPartOfMultiVersionSource ? $"_UAPv{_requiredUapVersion}" : string.Empty);

            IReadOnlyList<LoadedImageSurfaceInfo> IAnimatedVisualInfo.LoadedImageSurfaceNodes
            {
                get
                {
                    if (_loadedImageSurfaceInfos == null)
                    {
                        _loadedImageSurfaceInfos =
                            (from n in _nodes
                             where n.IsLoadedImageSurface
                             select _owner._loadedImageSurfaceInfosByNode[n])
                                .OrderBy(n => n.Name, AlphanumericStringComparer.Instance)
                                .ToArray();
                    }

                    return _loadedImageSurfaceInfos;
                }
            }

            uint IAnimatedVisualInfo.RequiredUapVersion => _requiredUapVersion;
        }

        // Aggregates ObjectData nodes that are shared between different IAnimatedVisual instances,
        // for example, LoadedImageSurface objects. Such nodes describe factories that are
        // scoped to the IAnimatedVisualSource implementation rather than the IAnimatedVisual implementation.
        sealed class SharedNodeGroup
        {
            readonly ObjectData[] _items;

            internal SharedNodeGroup(IEnumerable<ObjectData> items)
            {
                _items = items.ToArray();
            }

            /// <summary>
            /// An <see cref="ObjectData"/> object that will be treated as the canonical object.
            /// </summary>
            internal ObjectData CanonicalNode => _items[0];

            /// <summary>
            /// The <see cref="ObjectData"/> objects except the <see cref="CanonicalNode"/> object.
            /// </summary>
            internal IEnumerable<ObjectData> Rest => _items.Skip(1);

            /// <summary>
            ///  All of the <see cref="ObjectData"/> objects that are sharing this group.
            /// </summary>
            internal IReadOnlyList<ObjectData> All => _items;
        }

        // A node in the object graph, annotated with extra stuff to assist in code generation.
        sealed class ObjectData : Graph.Node<ObjectData>
        {
            Func<string> _overriddenFactoryCall;
            Dictionary<ObjectData, string> _callFactoryFromForCache;

            public Dictionary<ObjectData, string> CallFactoryFromForCache
            {
                get
                {
                    // Lazy initialization because not all nodes need the cache.
                    if (_callFactoryFromForCache == null)
                    {
                        _callFactoryFromForCache = new Dictionary<ObjectData, string>();
                    }

                    return _callFactoryFromForCache;
                }
            }

            // The name that is given to the node by the NodeNamer. This name is used to generate factory method
            // names and field names.
            public string Name { get; set; }

            public string FieldName => RequiresStorage ? CamelCase(Name) : null;

            // Returns text for obtaining the value for this node. If the node has
            // been inlined, this can generate the code into the returned string, otherwise
            // it returns code for calling the factory.
            internal string FactoryCall()
                 => Inlined ? _overriddenFactoryCall() : $"{Name}()";

            IEnumerable<string> GetAncestorShortComments()
            {
                // Get the nodes that reference this node.
                var parents = InReferences.Select(v => v.Node).ToArray();
                if (parents.Length == 1)
                {
                    // There is exactly one parent. Get its comments.
                    if (string.IsNullOrWhiteSpace(parents[0].ShortComment))
                    {
                        // Parent has no comment.
                        yield break;
                    }

                    foreach (var ancestorShortcomment in parents[0].GetAncestorShortComments())
                    {
                        yield return ancestorShortcomment;
                    }

                    if (!string.IsNullOrWhiteSpace(parents[0].ShortComment))
                    {
                        yield return parents[0].ShortComment;
                    }
                }
            }

            internal string LongComment
            {
                get
                {
                    // Prepend the ancestor nodes.
                    var sb = new StringBuilder();
                    var ancestorIndent = 0;
                    foreach (var ancestorComment in GetAncestorShortComments())
                    {
                        sb.Append(new string(' ', ancestorIndent));
                        sb.AppendLine(ancestorComment);
                        ancestorIndent += 2;
                    }

                    sb.Append(((IDescribable)Object).LongDescription);

                    return sb.ToString();
                }
            }

            internal string ShortComment => ((IDescribable)Object).ShortDescription;

            // True if the object is referenced from more than one method and
            // therefore must be stored after it is created.
            internal bool RequiresStorage { get; set; }

            // True if the object must be stored as read-only after it is created.
            internal bool RequiresReadonlyStorage { get; set; }

            // Set to indicate that the node relies on Microsoft.Graphics.Canvas namespace
            internal bool UsesCanvas => Object is CompositionEffectBrush;

            // Set to indicate that the node relies on Microsoft.Graphics.Canvas.Effects namespace
            internal bool UsesCanvasEffects => Object is CompositionEffectBrush;

            // Set to indicate that the node relies on Microsoft.Graphics.Canvas.Geometry namespace
            internal bool UsesCanvasGeometry => Object is CanvasGeometry;

            // Set to indicate that the node is a LoadedImageSurface.
            internal bool IsLoadedImageSurface => Object is Wmd.LoadedImageSurface;

            // True if the node describes an object that can be shared between
            // multiple IAnimatedVisual classes, and thus will be associated with the
            // IAnimatedVisualSource implementation rather than the IAnimatedVisual implementation.
            internal bool IsSharedNode { get; set; }

            // Set to indicate that the node uses the Windows.UI.Xaml.Media namespace.
            internal bool UsesNamespaceWindowsUIXamlMedia => IsLoadedImageSurface;

            // Set to indicate that the node uses stream(s).
            internal bool UsesStream => Object is Wmd.LoadedImageSurface lis && lis.Type == Wmd.LoadedImageSurface.LoadedImageSurfaceType.FromStream;

            // Set to indicate that the node uses asset file(s).
            internal bool UsesAssetFile => Object is Wmd.LoadedImageSurface lis && lis.Type == Wmd.LoadedImageSurface.LoadedImageSurfaceType.FromUri;

            // Set to indicate that the composition depends on a composite effect.
            internal bool UsesCompositeEffect => Object is CompositionEffectBrush compositeEffectBrush && compositeEffectBrush.GetEffect().Type == Mgce.GraphicsEffectType.CompositeEffect;

            // Identifies the byte array of a LoadedImageSurface.
            internal string LoadedImageSurfaceBytesFieldName => IsLoadedImageSurface ? $"s_{Name}_Bytes" : null;

            internal Uri LoadedImageSurfaceImageUri { get; set; }

            // True if the code to create the object will be generated inline.
            internal bool Inlined => _overriddenFactoryCall != null;

            internal void ForceInline(Func<string> replacementFactoryCall)
            {
                _overriddenFactoryCall = replacementFactoryCall;
                RequiresStorage = false;
                RequiresReadonlyStorage = false;
            }

            // The name of the type of the object described by this node.
            // This is the name used as the return type of a factory method.
            internal string TypeName
                => Type switch
                {
                    Graph.NodeType.CompositionObject => ((CompositionObject)Object).Type.ToString(),
                    Graph.NodeType.CompositionPath => "CompositionPath",
                    Graph.NodeType.CanvasGeometry => "CanvasGeometry",
                    Graph.NodeType.LoadedImageSurface => "LoadedImageSurface",
                    _ => throw new InvalidOperationException(),
                };

            // True iff a factory should be created for the given node.
            internal bool NeedsAFactory
            {
                get
                {
                    return !Inlined && Type switch
                    {
                        Graph.NodeType.CanvasGeometry => true,
                        Graph.NodeType.CompositionPath => true,
                        Graph.NodeType.LoadedImageSurface => true,
                        Graph.NodeType.CompositionObject => NeedsAFactory((CompositionObject)Object),
                        _ => throw new InvalidOperationException(),
                    };

                    bool NeedsAFactory(CompositionObject obj)
                    {
                        return obj.Type switch
                        {
                            // AnimationController is never created explicitly - they result from
                            // calling TryGetAnimationController(...).
                            CompositionObjectType.AnimationController => false,

                            // CompositionPropertySet is never created explicitly - they just exist
                            // on the Properties property of every CompositionObject.
                            CompositionObjectType.CompositionPropertySet => false,

                            // ExpressionAnimations that are not shared will use the "_reusableExpressionAnimation"
                            // so there is no need for a factory for them. Detect the shared case by counting the
                            // InReferences to the node.
                            CompositionObjectType.ExpressionAnimation => InReferences.Length > 1,

                            // All other CompositionObjects need factories.
                            _ => true,
                        };
                    }
                }
            }

            // For debugging purposes only.
            public override string ToString() => Name == null ? $"{TypeName} {Position}" : $"{Name} {Position}";

            // Sets the first character to lower case.
            static string CamelCase(string value) => $"_{char.ToLowerInvariant(value[0])}{value.Substring(1)}";
        }
    }
}
