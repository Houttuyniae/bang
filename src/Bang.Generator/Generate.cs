﻿using Bang.Components;
using Bang.Interactions;
using Bang.StateMachines;
using Murder.Generator.Serialization;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Generator
{
    internal partial class Generation
    {
        private const string _template = "template.txt";
        
        /// <summary>
        /// This is the expected name of the file with intermediate information of already scanned data types.
        /// </summary>
        private const string _intermediateFile = ".components";

        private string IntermediateFile(string path) => Path.Join(path, _intermediateFile);

        private readonly string _targetNamespace;
        private readonly ImmutableArray<Assembly> _targetAssemblies;

        internal Generation(string targetAssembly, IEnumerable<Assembly> targetAssemblies)
        {
            _targetNamespace = targetAssembly;
            _targetAssemblies = targetAssemblies.ToImmutableArray();
        }

        internal async ValueTask GenerateIntermediate(string pathToIntermediate, string outputDirectory)
        {
            var componentsDescriptions =
                GetComponentsDescription(out var genericComponentsDescription, out int lastAvailableIndex);

            var messagesDescriptions = 
                GetMessagesDescription(lastAvailableIndex);

            string path = IntermediateFile(pathToIntermediate);
            Descriptor descriptor = new(
                _targetNamespace,
                componentsDescriptions.ToDictionary(c => c.Name, c => c),
                messagesDescriptions.ToDictionary(m => m.Name, m => m),
                genericComponentsDescription.ToDictionary(m => m.GetName(), m => m));

            string parentPath = IntermediateFile(outputDirectory);
            if (File.Exists(parentPath))
            {
                Descriptor? parentDescriptor = await SerializationHelper.DeserializeAsDescriptor(parentPath);
                ProcessParentDescriptor(parentDescriptor, ref descriptor);
            }

            await SerializationHelper.Serialize(descriptor, path);
        }

        /// <summary>
        /// This will check for any references that were already considered in the parent path.
        /// </summary>
        private void ProcessParentDescriptor(Descriptor? parentDescriptor, ref Descriptor descriptor)
        {
            if (parentDescriptor is null || parentDescriptor.Namespace == _targetNamespace)
            {
                // It is actually the same intermediate file. Skip processing this.
                return;
            }

            descriptor.ParentDescriptor = parentDescriptor;

            ComponentDescriptor[] components = parentDescriptor.ComponentsWithParent();
            ComponentDescriptor[] messages = parentDescriptor.MessagesWithParent();
            GenericComponentDescriptor[] generics = parentDescriptor.GenericsWithParent();

            HashSet<int> indices = new();
            foreach (ComponentDescriptor c in components)
            {
                indices.Add(c.Index);

                descriptor.ComponentsMap.Remove(c.Name);
            }

            foreach (ComponentDescriptor m in messages)
            {
                indices.Add(m.Index);

                descriptor.MessagesMap.Remove(m.Name);
            }

            foreach (GenericComponentDescriptor g in generics)
            {
                indices.Add(g.Index);

                descriptor.GenericsMap.Remove(g.GetName());
            }

            // Now, shift the indices of all the components we skipped.
            int shift = indices.Count;
            int recountIndex = 0;

            foreach (string name in descriptor.ComponentsMap.Keys)
            {
                descriptor.ComponentsMap[name].Index = recountIndex + shift;

                recountIndex++;
            }

            foreach (string name in descriptor.MessagesMap.Keys)
            {
                descriptor.MessagesMap[name].Index = recountIndex + shift;

                recountIndex++;
            }

            foreach (string name in descriptor.GenericsMap.Keys)
            {
                // For each of the generic components, we will try to map to its correspondent interface match.
                // This is done for IInteractiveComponent and IStateMachineComponent, which will point to the same index
                // of all their implementations.
                foreach (Type @interface in descriptor.GenericsMap[name].InstanceType.GetInterfaces())
                {
                    if (parentDescriptor.ComponentsMap.TryGetValue(Prettify(@interface), out ComponentDescriptor? interfaceForGenericComponent))
                    {
                        descriptor.GenericsMap[name].Index = interfaceForGenericComponent.Index;

                        // Found it!
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Generate the code for the Bang components.
        /// </summary>
        /// <param name="pathToIntermediate">Path to the intermediate file to read from.</param>
        /// <param name="generatedFileDirectory">Path to the source directory which will point to EntityExtensions.cs file.</param>
        internal async ValueTask Generate(string generatedFileDirectory)
        {
            Descriptor descriptor = 
                await SerializationHelper.DeserializeAsDescriptor(IntermediateFile(generatedFileDirectory));

            string outputFilePath = Path.Combine(generatedFileDirectory, "EntityExtensions.cs");
            string templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _template);

            string targetAssemblyEscaped = ClassName(_targetNamespace);

            var (componentsDescriptions, messagesDescriptions, genericComponentsDescription) =
                (descriptor.Components, descriptor.Messages, descriptor.Generics);

            var (componentsDescriptionsWithParent, messagesDescriptionsWithParent, genericComponentsDescriptionWithParent) =
                (descriptor.ComponentsWithParent(), descriptor.MessagesWithParent(), descriptor.GenericsWithParent());

            IEnumerable<Type?> targetTypes =
                componentsDescriptionsWithParent.Select(t => t.Type)
                .Concat(genericComponentsDescriptionWithParent.Select(t => t.InstanceType))
                .Concat(genericComponentsDescriptionWithParent.Select(t => t.GenericArgument))
                .Concat(messagesDescriptionsWithParent.Select(t => t.Type));

            Dictionary<string, string> parameters = new()
            {
                { "<target_assembly>", targetAssemblyEscaped },
                { "<using_namespaces>", GenerateNamespaces(targetTypes) },
                { "<components_enum>", GenerateEnums(componentsDescriptions) },
                { "<messages_enum>", GenerateEnums(messagesDescriptions) },
                { "<components_get>", GenerateComponentsGetter(componentsDescriptions) },
                { "<components_has>", GenerateComponentsHas(componentsDescriptions) },
                { "<components_tryget>", GenerateComponentsTryGet(componentsDescriptions) },
                { "<components_set>", GenerateComponentsSet(componentsDescriptions) },
                { "<components_remove>", GenerateComponentsRemove(componentsDescriptions) },
                { "<messages_has>", GenerateMessagesHas(messagesDescriptions) },
                { "<lookup_parent>", GetComponentsLookupParentName(descriptor) },
                { "<components_relative_set>", GenerateRelativeSet(componentsDescriptionsWithParent) },
                { "<components_type_to_index>", GenerateTypesDictionary(componentsDescriptionsWithParent, genericComponentsDescriptionWithParent) },
                { "<messages_type_to_index>", GenerateTypesDictionary(messagesDescriptionsWithParent) }
            };

            string template = await File.ReadAllTextAsync(templatePath);
            string formatted = parameters.Aggregate(template, 
                (current, parameter) => current.Replace(parameter.Key, parameter.Value));

            await File.WriteAllTextAsync(outputFilePath, formatted);
        }

        private string ClassName(string name) => name.Replace('.', '_');

        private string GetComponentsLookupParentName(Descriptor descriptor) =>
            descriptor.ParentDescriptor is null ? "ComponentsLookup" : $"{ClassName(descriptor.ParentDescriptor.Namespace)}LookupImplementation";

        private List<ComponentDescriptor> GetMessagesDescription(int startingIndex)
        {
            IEnumerable<Type> messages = ReflectionHelper.GetAllCandidateMessages(_targetAssemblies);

            int index = startingIndex;
            return messages.Select(m => new ComponentDescriptor(index++, Prettify(m), m)).ToList();
        }

        private List<ComponentDescriptor> GetComponentsDescription(
            out List<GenericComponentDescriptor> generics,
            out int lastAvailableIndex)
        {
            IEnumerable<Type> components = ReflectionHelper.GetAllCandidateComponents(_targetAssemblies);

            Dictionary<Type, int> lookup = new();
            List<ComponentDescriptor> result = new();

            int index = 0;
            foreach (Type t in components)
            {
                lookup[t] = index++;
                result.Add(new(lookup[t], Prettify(t), t));
            }

            generics = new();

            Type genericStateMachineComponent = typeof(StateMachineComponent<>);

            Type stateMachineComponent = typeof(IStateMachineComponent);
            IEnumerable<Type> allStateMachines = ReflectionHelper.GetAllStateMachineComponents(_targetAssemblies);

            if (allStateMachines.Any())
            {
                if (!lookup.ContainsKey(stateMachineComponent))
                {
                    // Generic has not been added yet.
                    lookup[stateMachineComponent] = index++;
                    result.Add(new(lookup[stateMachineComponent], Prettify(stateMachineComponent), stateMachineComponent));
                }
            }

            foreach (Type t in allStateMachines)
            {
                generics.Add(new(lookup[stateMachineComponent], genericStateMachineComponent, t));
            }

            Type interactiveComponent = typeof(IInteractiveComponent);
            Type genericInteractiveComponent = typeof(InteractiveComponent<>);

            foreach (Type t in ReflectionHelper.GetAllInteractionComponents(_targetAssemblies))
            {
                if (!lookup.ContainsKey(interactiveComponent))
                {
                    // Generic has not been added yet.
                    lookup[interactiveComponent] = index++;
                    result.Add(new(lookup[interactiveComponent], Prettify(interactiveComponent), interactiveComponent));
                }

                generics.Add(new(lookup[interactiveComponent], genericInteractiveComponent, t));
            }

            Type tTransformBaseInterface = typeof(ITransformComponent);
            Type tTransformComponent = ReflectionHelper.FindTransformInterfaceComponent(_targetAssemblies);
            
            foreach (Type t in ReflectionHelper.GetAllTransformComponents(_targetAssemblies))
            {
                if (!lookup.ContainsKey(tTransformComponent))
                {
                    // Interface has not been added yet.
                    lookup[tTransformComponent] = index++;
                    
                    result.Add(new(index: lookup[tTransformComponent], name: Prettify(tTransformBaseInterface), tTransformComponent));
                    result.Add(new(index: lookup[tTransformComponent], name: Prettify(tTransformBaseInterface) + "Base", tTransformBaseInterface));
                }

                generics.Add(new(lookup[tTransformComponent], t, genericArgument: null));
            }

            lastAvailableIndex = index;
            return result;
        }
        
        private string GenerateNamespaces(IEnumerable<Type?> types)
        {
            StringBuilder builder = new();

            HashSet<string> allNamespaces = new();

            foreach (Type? t in types)
            {
                if (t is not null && t.Namespace is string @namespace && !allNamespaces.Contains(@namespace))
                {
                    allNamespaces.Add(@namespace);
                }
            }

            foreach (string @namespace in allNamespaces)
            {
                builder.Append($"using {@namespace};\n");
            }

            // Trim last extra enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 1, 1);
            }

            return builder.ToString();
        }

        private string GenerateEnums(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.Append($"{desc.Name} = {desc.Index},\n");
            }

            // Trim last extra comma and enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateComponentsGetter(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.AppendFormat($"{ReflectionHelper.GetAccessModifier(desc.Type)} static {desc.Type.Name} Get{desc.Name}(this Entity e)\n");
                builder.AppendFormat("        {{\n");
                builder.AppendFormat($"            return e.GetComponent<{desc.Type.Name}>({desc.Index});\n");
                builder.AppendFormat("        }}\n\n");
            }

            // Trim last enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateComponentsHas(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (desc.Name is null)
                {
                    // Skip syntax sugar if there is no name.
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.AppendFormat($"{ReflectionHelper.GetAccessModifier(desc.Type)} static bool Has{desc.Name}(this Entity e)\n");
                builder.AppendFormat("        {{\n");
                builder.AppendFormat($"            return e.HasComponent({desc.Index});\n");
                builder.AppendFormat("        }}\n\n");
            }

            // Trim last enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateComponentsTryGet(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.AppendFormat($"{ReflectionHelper.GetAccessModifier(desc.Type)} static {desc.Type.Name}? TryGet{desc.Name}(this Entity e)\n");
                builder.AppendFormat("        {{\n");
                builder.AppendFormat($"            if (!e.Has{desc.Name}())\n");
                builder.AppendFormat("            {{\n");
                builder.AppendFormat($"                return null;\n");
                builder.AppendFormat("            }}\n\n");
                builder.AppendFormat($"            return e.Get{desc.Name}();\n");
                builder.AppendFormat("        }}\n\n");
            }

            // Trim last enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }
        
        private string GenerateComponentsSet(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.AppendFormat($"{ReflectionHelper.GetAccessModifier(desc.Type)} static void Set{desc.Name}(this Entity e, {desc.Type.Name} component)\n");
                builder.AppendFormat("        {{\n");
                builder.AppendFormat($"            e.AddOrReplaceComponent(component, {desc.Index});\n");
                builder.AppendFormat("        }}\n\n");

                ConstructorInfo[] constructors = desc.Type.GetConstructors();
                IEnumerable<ParameterInfo[]> constructorsParameters = constructors.Select(c => c.GetParameters());

                // Check if there are any default constructors already available.
                if (desc.Type.IsValueType && !constructorsParameters.Any(p => p.Length == 0))
                {
                    // Always add a default constructor by default.
                    constructorsParameters = constructorsParameters.Append(new ParameterInfo[0]);
                }

                // Create fancy constructors based on the component!
                foreach (ParameterInfo[] parameters in constructorsParameters)
                {
                    builder.AppendFormat($"        {ReflectionHelper.GetAccessModifier(desc.Type)} static void Set{desc.Name}(this Entity e");
                    foreach (ParameterInfo p in parameters)
                    {
                        string parameterName = p.ParameterType.IsGenericType ?
                            FormatGenericName(p.ParameterType, p.ParameterType.GetGenericArguments()) :
                            FormatNonGenericTypeName(p.ParameterType);

                        builder.Append($", {parameterName} {p.Name}");
                    }

                    builder.AppendFormat(")\n");
                    builder.AppendFormat("        {{\n");
                    builder.AppendFormat($"            e.AddOrReplaceComponent(new {desc.Type.Name}(");
                    for (int c = 0; c < parameters.Length; c++)
                    {
                        builder.Append($"{parameters[c].Name}");

                        if (c != parameters.Length - 1)
                        {
                            builder.Append($", ");
                        }
                    }

                    builder.AppendFormat($"), {desc.Index});\n");
                    builder.AppendFormat("        }}\n\n");
                }
            }

            // Trim last enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateComponentsRemove(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.AppendFormat($"{ReflectionHelper.GetAccessModifier(desc.Type)} static bool Remove{desc.Name}(this Entity e)\n");
                builder.AppendFormat("        {{\n");
                builder.AppendFormat($"            return e.RemoveComponent({desc.Index});\n");
                builder.AppendFormat("        }}\n\n");
            }

            // Trim last enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateMessagesHas(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (desc.Name is null)
                {
                    // Skip syntax sugar if there is no name.
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("        ");
                }

                builder.AppendFormat($"{ReflectionHelper.GetAccessModifier(desc.Type)} static bool Has{desc.Name}Message(this Entity e)\n");
                builder.AppendFormat("        {{\n");
                builder.AppendFormat($"            return e.HasMessage({desc.Index});\n");
                builder.AppendFormat("        }}\n\n");
            }

            // Trim last enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateRelativeSet(IEnumerable<ComponentDescriptor> descriptions)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (!typeof(IParentRelativeComponent).IsAssignableFrom(desc.Type))
                {
                    // Not a relative component.
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("            ");
                }

                builder.AppendFormat($"{desc.Index},\n");
            }

            // Trim last comma+enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string GenerateTypesDictionary(
            IEnumerable<ComponentDescriptor> descriptions,
            IEnumerable<GenericComponentDescriptor>? generics = default)
        {
            StringBuilder builder = new();

            foreach (ComponentDescriptor desc in descriptions)
            {
                if (builder.Length > 0)
                {
                    builder.Append("            ");
                }

                builder.Append("{ ");
                builder.AppendFormat($"typeof({desc.Type.Name}), {desc.Index}");
                builder.Append(" },\n");
            }

            if (generics is not null)
            {
                foreach (GenericComponentDescriptor g in generics)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append("            ");
                    }

                    builder.Append("{ ");
                    builder.AppendFormat("typeof({0}), {1}",
                        g.GetName(), 
                        g.Index);
                    builder.Append(" },\n");
                }
            }

            // Trim last comma+enter.
            if (builder.Length > 0)
            {
                builder.Remove(builder.Length - 2, 2);
            }

            return builder.ToString();
        }

        private string FormatNonGenericTypeName(Type type)
        {
            StringBuilder builder = new();

            string name = type.FullName!;
            if (name.Contains("&"))
            {
                builder.Append($"in {name.Substring(0, name.LastIndexOf("&", StringComparison.InvariantCulture))}");
            }
            else
            {
                builder.Append(name);
            }

            return builder.ToString();
        }

        private string FormatGenericName(Type genericType, params Type[] genericArguments)
        {
            StringBuilder builder = new();

            string fullname = genericType.FullName!;

            builder.AppendFormat("{0}<",
                fullname.Substring(0, fullname.LastIndexOf("`", StringComparison.InvariantCulture)));
            for (int a = 0; a < genericArguments.Length; a++)
            {
                builder.AppendFormat($"{genericArguments[a].FullName}");

                if (a != genericArguments.Length - 1)
                {
                    builder.Append(", ");
                }
            }

            builder.Append(">");

            // So far, this cover cases such as:
            // System.Collections.Immutable.ImmutableDictionary`2+Builder[[...]]
            // There might be other edge cases that we might need to implement here.
            int indexAfterGeneric = fullname.IndexOf('+');
            if (indexAfterGeneric != -1)
            {
                int end = fullname.IndexOf('[');
                builder.AppendFormat(".{0}",
                    fullname.Substring(indexAfterGeneric + 1, end - indexAfterGeneric - 1));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Prettify the name of <paramref name="t"/>.
        /// </summary>
        internal static string Prettify(Type t)
        {
            StringBuilder builder = new(t.Name);
            if (t.IsInterface && builder[0] == 'I')
            {
                // If this is an interface, skip the "I" character.
                builder.Remove(0, 1);
            }

            string name = builder.ToString();

            // Remove "Component" of the name.
            Regex re = new Regex(@"(.*)(?=Component|Message)");
            Match m = re.Match(name);
            if (m.Success)
            {
                name = m.Groups[0].Value;
            }

            return name;
        }
    }
}