﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Serialization;
using Microsoft.CodeAnalysis.Storage;
using Microsoft.CodeAnalysis.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols
{
    internal partial class SymbolTreeInfo
    {
        private static string GetMetadataNameWithoutBackticks(MetadataReader reader, StringHandle name)
        {
            var blobReader = reader.GetBlobReader(name);
            var backtickIndex = blobReader.IndexOf((byte)'`');
            if (backtickIndex == -1)
            {
                return reader.GetString(name);
            }

            unsafe
            {
                return MetadataStringDecoder.DefaultUTF8.GetString(
                    blobReader.CurrentPointer, backtickIndex);
            }
        }

        public static MetadataId? GetMetadataIdNoThrow(PortableExecutableReference reference)
        {
            try
            {
                return reference.GetMetadataId();
            }
            catch (Exception e) when (e is BadImageFormatException or IOException)
            {
                return null;
            }
        }

        private static Metadata? GetMetadataNoThrow(PortableExecutableReference reference)
        {
            try
            {
                return reference.GetMetadata();
            }
            catch (Exception e) when (e is BadImageFormatException or IOException)
            {
                return null;
            }
        }

        public static ValueTask<SymbolTreeInfo> GetInfoForMetadataReferenceAsync(
            Solution solution, PortableExecutableReference reference, CancellationToken cancellationToken)
        {
            var checksum = GetMetadataChecksum(solution.Services, reference, cancellationToken);
            return GetInfoForMetadataReferenceAsync(solution, reference, checksum, cancellationToken);
        }

        /// <summary>
        /// Produces a <see cref="SymbolTreeInfo"/> for a given <see cref="PortableExecutableReference"/>.
        /// Note:  will never return null;
        /// </summary>
        [PerformanceSensitive("https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1224834", OftenCompletesSynchronously = true)]
        private static async ValueTask<SymbolTreeInfo> GetInfoForMetadataReferenceAsync(
            Solution solution,
            PortableExecutableReference reference,
            Checksum checksum,
            CancellationToken cancellationToken)
        {
            var metadataId = GetMetadataIdNoThrow(reference);
            if (metadataId == null)
                return CreateEmpty(checksum);

            if (s_metadataIdToInfo.TryGetValue(metadataId, out var infoTask))
            {
                var info = await infoTask.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (info.Checksum == checksum)
                    return info;
            }

            var metadata = GetMetadataNoThrow(reference);
            if (metadata == null)
                return CreateEmpty(checksum);

            return await GetInfoForMetadataReferenceSlowAsync(
                solution.Services, SolutionKey.ToSolutionKey(solution), reference, metadata, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<SymbolTreeInfo?> TryGetCachedInfoForMetadataReferenceIgnoreChecksumAsync(PortableExecutableReference reference, CancellationToken cancellationToken)
        {
            var metadataId = GetMetadataIdNoThrow(reference);
            if (metadataId == null || !s_metadataIdToInfo.TryGetValue(metadataId, out var infoTask))
                return null;

            return await infoTask.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<SymbolTreeInfo> GetInfoForMetadataReferenceSlowAsync(
            SolutionServices services,
            SolutionKey solutionKey,
            PortableExecutableReference reference,
            Metadata metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Important: this captured async lazy may live a long time *without* computing the final results. As such,
            // it is important that it note capture any large state.  For example, it should not hold onto a Solution
            // instance.
            var asyncLazy = s_metadataIdToInfo.GetValue(
                metadata.Id,
                id => new AsyncLazy<SymbolTreeInfo>(
                    c => CreateMetadataSymbolTreeInfoAsync(services, solutionKey, reference, c),
                    cacheResult: true));

            return await asyncLazy.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }

        [PerformanceSensitive("https://github.com/dotnet/roslyn/issues/33131", AllowCaptures = false)]
        public static Checksum GetMetadataChecksum(
            SolutionServices services, PortableExecutableReference reference, CancellationToken cancellationToken)
        {
            // We can reuse the index for any given reference as long as it hasn't changed.
            // So our checksum is just the checksum for the PEReference itself.
            // First see if the value is already in the cache, to avoid an allocation if possible.
            if (ChecksumCache.TryGetValue(reference, out var cached))
            {
                return cached;
            }

            // Break things up to the fast path above and this slow path where we allocate a closure.
            return GetMetadataChecksumSlow(services, reference, cancellationToken);
        }

        private static Checksum GetMetadataChecksumSlow(SolutionServices services, PortableExecutableReference reference, CancellationToken cancellationToken)
        {
            return ChecksumCache.GetOrCreate(reference, _ =>
            {
                var serializer = services.GetRequiredService<ISerializerService>();
                var checksum = serializer.CreateChecksum(reference, cancellationToken);

                // Include serialization format version in our checksum.  That way if the 
                // version ever changes, all persisted data won't match the current checksum
                // we expect, and we'll recompute things.
                return Checksum.Create(checksum, SerializationFormatChecksum);
            });
        }

        private static string GetMetadataKeySuffix(PortableExecutableReference reference)
            => "_Metadata_" + reference.FilePath;

        private static Task<SymbolTreeInfo> CreateMetadataSymbolTreeInfoAsync(
            SolutionServices services,
            SolutionKey solutionKey,
            PortableExecutableReference reference,
            CancellationToken cancellationToken)
        {
            return LoadOrCreateAsync(
                services,
                solutionKey,
                getChecksumAsync: () => new ValueTask<Checksum>(GetMetadataChecksum(services, reference, cancellationToken)),
                createAsync: checksum => new ValueTask<SymbolTreeInfo>(CreateMetadataSymbolTreeInfo(checksum, reference)),
                keySuffix: GetMetadataKeySuffix(reference),
                cancellationToken);
        }

        /// <summary>
        /// Loads any info we have for this reference from our persistence store.  Will succeed regardless of the
        /// checksum of the <paramref name="reference"/>.  Should only be used by clients that are ok with potentially
        /// stale data.
        /// </summary>
        public static Task<SymbolTreeInfo> LoadAnyInfoForMetadataReferenceAsync(
            Solution solution,
            PortableExecutableReference reference,
            CancellationToken cancellationToken)
        {
            return LoadAsync(
                solution.Services,
                SolutionKey.ToSolutionKey(solution),
                checksum: GetMetadataChecksum(solution.Services, reference, cancellationToken),
                checksumMustMatch: false,
                keySuffix: GetMetadataKeySuffix(reference),
                cancellationToken);
        }

        private static SymbolTreeInfo CreateMetadataSymbolTreeInfo(
            Checksum checksum, PortableExecutableReference reference)
        {
            var creator = new MetadataInfoCreator(checksum, reference);
            return creator.Create();
        }

        private struct MetadataInfoCreator : IDisposable
        {
            private static readonly Predicate<string> s_isNotNullOrEmpty = s => !string.IsNullOrEmpty(s);
            private static readonly ObjectPool<List<string>> s_stringListPool = SharedPools.Default<List<string>>();

            private readonly Checksum _checksum;
            private readonly PortableExecutableReference _reference;

            private readonly OrderPreservingMultiDictionary<string, string> _inheritanceMap;
            private readonly OrderPreservingMultiDictionary<MetadataNode, MetadataNode> _parentToChildren;
            private readonly MetadataNode _rootNode;

            // The set of type definitions we've read out of the current metadata reader.
            private readonly List<MetadataDefinition> _allTypeDefinitions = new();

            // Map from node represents extension method to list of possible parameter type info.
            // We can have more than one if there's multiple methods with same name but different receiver type.
            // e.g.
            //
            //      public static bool AnotherExtensionMethod1(this int x);
            //      public static bool AnotherExtensionMethod1(this bool x);
            //
            private readonly MultiDictionary<MetadataNode, ParameterTypeInfo> _extensionMethodToParameterTypeInfo = new();
            private bool _containsExtensionsMethod;

            public MetadataInfoCreator(
                Checksum checksum, PortableExecutableReference reference)
            {
                _checksum = checksum;
                _reference = reference;
                _containsExtensionsMethod = false;

                _inheritanceMap = OrderPreservingMultiDictionary<string, string>.GetInstance();
                _parentToChildren = OrderPreservingMultiDictionary<MetadataNode, MetadataNode>.GetInstance();
                _rootNode = MetadataNode.Allocate(name: "");
            }

            private static ImmutableArray<ModuleMetadata> GetModuleMetadata(Metadata? metadata)
            {
                try
                {
                    if (metadata is AssemblyMetadata assembly)
                    {
                        return assembly.GetModules();
                    }
                    else if (metadata is ModuleMetadata module)
                    {
                        return ImmutableArray.Create(module);
                    }
                }
                catch (BadImageFormatException)
                {
                    // Trying to get the modules of an assembly can throw.  For example, if 
                    // there is an invalid public-key defined for the assembly.  See:
                    // https://devdiv.visualstudio.com/DevDiv/_workitems?id=234447
                }

                return ImmutableArray<ModuleMetadata>.Empty;
            }

            internal SymbolTreeInfo Create()
            {
                foreach (var moduleMetadata in GetModuleMetadata(GetMetadataNoThrow(_reference)))
                {
                    try
                    {
                        var metadataReader = moduleMetadata.GetMetadataReader();

                        // First, walk all the symbols from metadata, populating the parentToChilren
                        // map accordingly.
                        GenerateMetadataNodes(metadataReader);

                        // Now, once we populated the initial map, go and get all the inheritance 
                        // information for all the types in the metadata.  This may refer to 
                        // types that we haven't seen yet.  We'll add those types to the parentToChildren
                        // map accordingly.
                        PopulateInheritanceMap(metadataReader);

                        // Clear the set of type definitions we read out of this piece of metadata.
                        _allTypeDefinitions.Clear();
                    }
                    catch (BadImageFormatException)
                    {
                        // any operation off metadata can throw BadImageFormatException
                        continue;
                    }
                }

                var receiverTypeNameToExtensionMethodMap = new MultiDictionary<string, ExtensionMethodInfo>();
                var unsortedNodes = GenerateUnsortedNodes(receiverTypeNameToExtensionMethodMap);

                return CreateSymbolTreeInfo(
                    _checksum, unsortedNodes, _inheritanceMap, receiverTypeNameToExtensionMethodMap);
            }

            public void Dispose()
            {
                // Return all the metadata nodes back to the pool so that they can be
                // used for the next PEReference we read.
                foreach (var (_, children) in _parentToChildren)
                {
                    foreach (var child in children)
                        MetadataNode.Free(child);
                }

                MetadataNode.Free(_rootNode);

                _parentToChildren.Free();
                _inheritanceMap.Free();
            }

            private void GenerateMetadataNodes(MetadataReader metadataReader)
            {
                var globalNamespace = metadataReader.GetNamespaceDefinitionRoot();
                var definitionMap = OrderPreservingMultiDictionary<string, MetadataDefinition>.GetInstance();
                try
                {
                    LookupMetadataDefinitions(metadataReader, globalNamespace, definitionMap);

                    foreach (var (name, definitions) in definitionMap)
                        GenerateMetadataNodes(metadataReader, _rootNode, name, definitions);
                }
                finally
                {
                    definitionMap.Free();
                }
            }

            private void GenerateMetadataNodes(
                MetadataReader metadataReader,
                MetadataNode parentNode,
                string nodeName,
                OrderPreservingMultiDictionary<string, MetadataDefinition>.ValueSet definitionsWithSameName)
            {
                if (!UnicodeCharacterUtilities.IsValidIdentifier(nodeName))
                {
                    return;
                }

                var childNode = MetadataNode.Allocate(nodeName);
                _parentToChildren.Add(parentNode, childNode);

                // Add all child members
                var definitionMap = OrderPreservingMultiDictionary<string, MetadataDefinition>.GetInstance();
                try
                {
                    foreach (var definition in definitionsWithSameName)
                    {
                        if (definition.Kind == MetadataDefinitionKind.Member)
                        {
                            // We need to support having multiple methods with same name but different receiver type.
                            _extensionMethodToParameterTypeInfo.Add(childNode, definition.ReceiverTypeInfo);
                        }

                        LookupMetadataDefinitions(metadataReader, definition, definitionMap);
                    }

                    foreach (var (name, definitions) in definitionMap)
                        GenerateMetadataNodes(metadataReader, childNode, name, definitions);
                }
                finally
                {
                    definitionMap.Free();
                }
            }

            private void LookupMetadataDefinitions(
                MetadataReader metadataReader,
                MetadataDefinition definition,
                OrderPreservingMultiDictionary<string, MetadataDefinition> definitionMap)
            {
                switch (definition.Kind)
                {
                    case MetadataDefinitionKind.Namespace:
                        LookupMetadataDefinitions(metadataReader, definition.Namespace, definitionMap);
                        break;
                    case MetadataDefinitionKind.Type:
                        LookupMetadataDefinitions(metadataReader, definition.Type, definitionMap);
                        break;
                }
            }

            private void LookupMetadataDefinitions(
                MetadataReader metadataReader,
                TypeDefinition typeDefinition,
                OrderPreservingMultiDictionary<string, MetadataDefinition> definitionMap)
            {
                // Only bother looking for extension methods in static types.
                // Note this check means we would ignore extension methods declared in assemblies
                // compiled from VB code, since a module in VB is compiled into class with 
                // "sealed" attribute but not "abstract". 
                // Although this can be addressed by checking custom attributes,
                // we believe this is not a common scenario to warrant potential perf impact.
                if ((typeDefinition.Attributes & TypeAttributes.Abstract) != 0 &&
                    (typeDefinition.Attributes & TypeAttributes.Sealed) != 0)
                {
                    foreach (var child in typeDefinition.GetMethods())
                    {
                        var method = metadataReader.GetMethodDefinition(child);
                        if ((method.Attributes & MethodAttributes.SpecialName) != 0 ||
                            (method.Attributes & MethodAttributes.RTSpecialName) != 0)
                        {
                            continue;
                        }

                        // SymbolTreeInfo is only searched for types and extension methods.
                        // So we don't want to pull in all methods here.  As a simple approximation
                        // we just pull in methods that have attributes on them.
                        if ((method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                            (method.Attributes & MethodAttributes.Static) != 0 &&
                            method.GetParameters().Count > 0 &&
                            method.GetCustomAttributes().Count > 0)
                        {
                            // Decode method signature to get the receiver type name (i.e. type name for the first parameter)
                            var blob = metadataReader.GetBlobReader(method.Signature);
                            var decoder = new SignatureDecoder<ParameterTypeInfo, object?>(ParameterTypeInfoProvider.Instance, metadataReader, genericContext: null);
                            var signature = decoder.DecodeMethodSignature(ref blob);

                            // It'd be good if we don't need to go through all parameters and make unnecessary allocations.
                            // However, this is not possible with meatadata reader API right now (although it's possible by copying code from meatadata reader implementaion)
                            if (signature.ParameterTypes.Length > 0)
                            {
                                _containsExtensionsMethod = true;
                                var firstParameterTypeInfo = signature.ParameterTypes[0];
                                var definition = new MetadataDefinition(MetadataDefinitionKind.Member, metadataReader.GetString(method.Name), firstParameterTypeInfo);
                                definitionMap.Add(definition.Name, definition);
                            }
                        }
                    }
                }

                foreach (var child in typeDefinition.GetNestedTypes())
                {
                    var type = metadataReader.GetTypeDefinition(child);

                    // We don't include internals from metadata assemblies.  It's less likely that
                    // a project would have IVT to it and so it helps us save on memory.  It also
                    // means we can avoid loading lots and lots of obfuscated code in the case the
                    // dll was obfuscated.
                    if (IsPublic(type.Attributes))
                    {
                        var definition = MetadataDefinition.Create(metadataReader, type);
                        definitionMap.Add(definition.Name, definition);
                        _allTypeDefinitions.Add(definition);
                    }
                }
            }

            private void LookupMetadataDefinitions(
                MetadataReader metadataReader,
                NamespaceDefinition namespaceDefinition,
                OrderPreservingMultiDictionary<string, MetadataDefinition> definitionMap)
            {
                foreach (var child in namespaceDefinition.NamespaceDefinitions)
                {
                    var definition = MetadataDefinition.Create(metadataReader, child);
                    definitionMap.Add(definition.Name, definition);
                }

                foreach (var child in namespaceDefinition.TypeDefinitions)
                {
                    var typeDefinition = metadataReader.GetTypeDefinition(child);
                    if (IsPublic(typeDefinition.Attributes))
                    {
                        var definition = MetadataDefinition.Create(metadataReader, typeDefinition);
                        definitionMap.Add(definition.Name, definition);
                        _allTypeDefinitions.Add(definition);
                    }
                }
            }

            private static bool IsPublic(TypeAttributes attributes)
            {
                var masked = attributes & TypeAttributes.VisibilityMask;
                return masked is TypeAttributes.Public or TypeAttributes.NestedPublic;
            }

            private void PopulateInheritanceMap(MetadataReader metadataReader)
            {
                foreach (var typeDefinition in _allTypeDefinitions)
                {
                    Debug.Assert(typeDefinition.Kind == MetadataDefinitionKind.Type);
                    PopulateInheritance(metadataReader, typeDefinition);
                }
            }

            private void PopulateInheritance(
                MetadataReader metadataReader,
                MetadataDefinition metadataTypeDefinition)
            {
                var derivedTypeDefinition = metadataTypeDefinition.Type;
                var interfaceImplHandles = derivedTypeDefinition.GetInterfaceImplementations();

                if (derivedTypeDefinition.BaseType.IsNil &&
                    interfaceImplHandles.Count == 0)
                {
                    return;
                }

                var derivedTypeSimpleName = metadataTypeDefinition.Name;

                PopulateInheritance(metadataReader, derivedTypeSimpleName, derivedTypeDefinition.BaseType);

                foreach (var interfaceImplHandle in interfaceImplHandles)
                {
                    if (!interfaceImplHandle.IsNil)
                    {
                        var interfaceImpl = metadataReader.GetInterfaceImplementation(interfaceImplHandle);
                        PopulateInheritance(metadataReader, derivedTypeSimpleName, interfaceImpl.Interface);
                    }
                }
            }

            private void PopulateInheritance(
                MetadataReader metadataReader,
                string derivedTypeSimpleName,
                EntityHandle baseTypeOrInterfaceHandle)
            {
                if (baseTypeOrInterfaceHandle.IsNil)
                {
                    return;
                }

                var baseTypeNameParts = s_stringListPool.Allocate();
                try
                {
                    AddBaseTypeNameParts(metadataReader, baseTypeOrInterfaceHandle, baseTypeNameParts);
                    if (baseTypeNameParts.Count > 0 &&
                        baseTypeNameParts.TrueForAll(s_isNotNullOrEmpty))
                    {
                        var lastPart = baseTypeNameParts.Last();
                        if (!_inheritanceMap.Contains(lastPart, derivedTypeSimpleName))
                        {
                            _inheritanceMap.Add(baseTypeNameParts.Last(), derivedTypeSimpleName);
                        }

                        // The parent/child map may not know about this base-type yet (for example,
                        // if the base type is a reference to a type outside of this assembly).
                        // Add the base type to our map so we'll be able to resolve it later if 
                        // requested. 
                        EnsureParentsAndChildren(baseTypeNameParts);
                    }
                }
                finally
                {
                    s_stringListPool.ClearAndFree(baseTypeNameParts);
                }
            }

            private void AddBaseTypeNameParts(
                MetadataReader metadataReader,
                EntityHandle baseTypeOrInterfaceHandle,
                List<string> simpleNames)
            {
                var typeDefOrRefHandle = GetTypeDefOrRefHandle(metadataReader, baseTypeOrInterfaceHandle);
                if (typeDefOrRefHandle.Kind == HandleKind.TypeDefinition)
                {
                    AddTypeDefinitionNameParts(metadataReader, (TypeDefinitionHandle)typeDefOrRefHandle, simpleNames);
                }
                else if (typeDefOrRefHandle.Kind == HandleKind.TypeReference)
                {
                    AddTypeReferenceNameParts(metadataReader, (TypeReferenceHandle)typeDefOrRefHandle, simpleNames);
                }
            }

            private void AddTypeDefinitionNameParts(
                MetadataReader metadataReader,
                TypeDefinitionHandle handle,
                List<string> simpleNames)
            {
                var typeDefinition = metadataReader.GetTypeDefinition(handle);
                var declaringType = typeDefinition.GetDeclaringType();
                if (declaringType.IsNil)
                {
                    // Not a nested type, just add the containing namespace.
                    AddNamespaceParts(metadataReader, typeDefinition.NamespaceDefinition, simpleNames);
                }
                else
                {
                    // We're a nested type, recurse and add the type we're declared in.
                    // It will handle adding the namespace properly.
                    AddTypeDefinitionNameParts(metadataReader, declaringType, simpleNames);
                }

                // Now add the simple name of the type itself.
                simpleNames.Add(GetMetadataNameWithoutBackticks(metadataReader, typeDefinition.Name));
            }

            private static void AddNamespaceParts(
                MetadataReader metadataReader,
                StringHandle namespaceHandle,
                List<string> simpleNames)
            {
                var blobReader = metadataReader.GetBlobReader(namespaceHandle);

                while (true)
                {
                    var dotIndex = blobReader.IndexOf((byte)'.');
                    unsafe
                    {
                        // Note: we won't get any string sharing as we're just using the 
                        // default string decoded.  However, that's ok.  We only produce
                        // these strings when we first read metadata.  Then we create and
                        // persist our own index.  In the future when we read in that index
                        // there's no way for us to share strings between us and the 
                        // compiler at that point.
                        if (dotIndex == -1)
                        {
                            simpleNames.Add(MetadataStringDecoder.DefaultUTF8.GetString(
                                blobReader.CurrentPointer, blobReader.RemainingBytes));
                            return;
                        }
                        else
                        {
                            simpleNames.Add(MetadataStringDecoder.DefaultUTF8.GetString(
                                blobReader.CurrentPointer, dotIndex));
                            blobReader.Offset += dotIndex + 1;
                        }
                    }
                }
            }

            private void AddNamespaceParts(
                MetadataReader metadataReader,
                NamespaceDefinitionHandle namespaceHandle,
                List<string> simpleNames)
            {
                if (namespaceHandle.IsNil)
                {
                    return;
                }

                var namespaceDefinition = metadataReader.GetNamespaceDefinition(namespaceHandle);
                AddNamespaceParts(metadataReader, namespaceDefinition.Parent, simpleNames);
                simpleNames.Add(metadataReader.GetString(namespaceDefinition.Name));
            }

            private static void AddTypeReferenceNameParts(
                MetadataReader metadataReader,
                TypeReferenceHandle handle,
                List<string> simpleNames)
            {
                var typeReference = metadataReader.GetTypeReference(handle);
                AddNamespaceParts(metadataReader, typeReference.Namespace, simpleNames);
                simpleNames.Add(GetMetadataNameWithoutBackticks(metadataReader, typeReference.Name));
            }

            private static EntityHandle GetTypeDefOrRefHandle(
                MetadataReader metadataReader,
                EntityHandle baseTypeOrInterfaceHandle)
            {
                switch (baseTypeOrInterfaceHandle.Kind)
                {
                    case HandleKind.TypeDefinition:
                    case HandleKind.TypeReference:
                        return baseTypeOrInterfaceHandle;
                    case HandleKind.TypeSpecification:
                        return FirstEntityHandleProvider.Instance.GetTypeFromSpecification(
                            metadataReader, (TypeSpecificationHandle)baseTypeOrInterfaceHandle);
                    default:
                        return default;
                }
            }

            private void EnsureParentsAndChildren(List<string> simpleNames)
            {
                var currentNode = _rootNode;

                foreach (var simpleName in simpleNames)
                {
                    var childNode = GetOrCreateChildNode(currentNode, simpleName);
                    currentNode = childNode;
                }
            }

            private MetadataNode GetOrCreateChildNode(
               MetadataNode currentNode, string simpleName)
            {
                if (_parentToChildren.TryGetValue(currentNode, static (childNode, simpleName) => childNode.Name == simpleName, simpleName, out var childNode))
                {
                    // Found an existing child node.  Just return that and all 
                    // future parts off of it.
                    return childNode;
                }

                // Couldn't find a child node with this name.  Make a new node for
                // it and return that for all future parts to be added to.
                var newChildNode = MetadataNode.Allocate(simpleName);
                _parentToChildren.Add(currentNode, newChildNode);
                return newChildNode;
            }

            private ImmutableArray<BuilderNode> GenerateUnsortedNodes(MultiDictionary<string, ExtensionMethodInfo> receiverTypeNameToMethodMap)
            {
                var unsortedNodes = ArrayBuilder<BuilderNode>.GetInstance();
                unsortedNodes.Add(BuilderNode.RootNode);

                AddUnsortedNodes(unsortedNodes, receiverTypeNameToMethodMap, parentNode: _rootNode, parentIndex: 0, fullyQualifiedContainerName: _containsExtensionsMethod ? "" : null);
                return unsortedNodes.ToImmutableAndFree();
            }

            private void AddUnsortedNodes(ArrayBuilder<BuilderNode> unsortedNodes,
                MultiDictionary<string, ExtensionMethodInfo> receiverTypeNameToMethodMap,
                MetadataNode parentNode,
                int parentIndex,
                string? fullyQualifiedContainerName)
            {
                foreach (var child in _parentToChildren[parentNode])
                {
                    var childNode = new BuilderNode(child.Name, parentIndex, _extensionMethodToParameterTypeInfo[child]);
                    var childIndex = unsortedNodes.Count;
                    unsortedNodes.Add(childNode);

                    if (fullyQualifiedContainerName != null)
                    {
                        foreach (var parameterTypeInfo in _extensionMethodToParameterTypeInfo[child])
                        {
                            // We do not differentiate array of different kinds for simplicity.
                            // e.g. int[], int[][], int[,], etc. are all represented as int[] in the index.
                            // similar for complex receiver types, "[]" means it's an array type, "" otherwise.
                            var parameterTypeName = (parameterTypeInfo.IsComplexType, parameterTypeInfo.IsArray) switch
                            {
                                (true, true) => Extensions.ComplexArrayReceiverTypeName,                          // complex array type, e.g. "T[,]"
                                (true, false) => Extensions.ComplexReceiverTypeName,                              // complex non-array type, e.g. "T"
                                (false, true) => parameterTypeInfo.Name + Extensions.ArrayReceiverTypeNameSuffix, // simple array type, e.g. "int[][,]"
                                (false, false) => parameterTypeInfo.Name                                          // simple non-array type, e.g. "int"
                            };

                            receiverTypeNameToMethodMap.Add(parameterTypeName, new ExtensionMethodInfo(fullyQualifiedContainerName, child.Name));
                        }
                    }

                    AddUnsortedNodes(unsortedNodes, receiverTypeNameToMethodMap, child, childIndex, Concat(fullyQualifiedContainerName, child.Name));
                }

                [return: NotNullIfNotNull("containerName")]
                static string? Concat(string? containerName, string name)
                {
                    if (containerName == null)
                    {
                        return null;
                    }

                    if (containerName.Length == 0)
                    {
                        return name;
                    }

                    return containerName + "." + name;
                }
            }
        }

        private class MetadataNode
        {
            private static readonly ObjectPool<MetadataNode> s_pool = SharedPools.Default<MetadataNode>();

            /// <summary>
            /// Represent this as non-null because that will be true when this is not in a pool and it is being used by
            /// other services.
            /// </summary>
            public string Name { get; private set; } = null!;

            public static MetadataNode Allocate(string name)
            {
                var node = s_pool.Allocate();
                Debug.Assert(node.Name == null);
                node.Name = name;
                return node;
            }

            public static void Free(MetadataNode node)
            {
                Debug.Assert(node.Name != null);
                node.Name = null!;
                s_pool.Free(node);
            }
        }

        private enum MetadataDefinitionKind
        {
            Namespace,
            Type,
            Member,
        }

        private readonly struct MetadataDefinition
        {
            public string Name { get; }
            public MetadataDefinitionKind Kind { get; }

            /// <summary>
            /// Only applies to member kind. Represents the type info of the first parameter.
            /// </summary>
            public ParameterTypeInfo ReceiverTypeInfo { get; }

            public NamespaceDefinition Namespace { get; }
            public TypeDefinition Type { get; }

            public MetadataDefinition(
                MetadataDefinitionKind kind,
                string name,
                ParameterTypeInfo receiverTypeInfo = default,
                NamespaceDefinition @namespace = default,
                TypeDefinition type = default)
            {
                Kind = kind;
                Name = name;
                ReceiverTypeInfo = receiverTypeInfo;
                Namespace = @namespace;
                Type = type;
            }

            public static MetadataDefinition Create(
                MetadataReader reader, NamespaceDefinitionHandle namespaceHandle)
            {
                var definition = reader.GetNamespaceDefinition(namespaceHandle);
                return new MetadataDefinition(
                    MetadataDefinitionKind.Namespace,
                    reader.GetString(definition.Name),
                    @namespace: definition);
            }

            public static MetadataDefinition Create(
                MetadataReader reader, TypeDefinition definition)
            {
                var typeName = GetMetadataNameWithoutBackticks(reader, definition.Name);
                return new MetadataDefinition(MetadataDefinitionKind.Type, typeName, type: definition);
            }
        }
    }
}
