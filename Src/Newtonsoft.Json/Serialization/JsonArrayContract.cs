#region License
// Copyright (c) 2007 James Newton-King
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Utilities;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

#if !HAVE_LINQ
using Newtonsoft.Json.Utilities.LinqBridge;
#else
using System.Linq;

#endif

namespace Newtonsoft.Json.Serialization
{
    /// <summary>
    /// Contract details for a <see cref="System.Type"/> used by the <see cref="JsonSerializer"/>.
    /// </summary>
    public class JsonArrayContract : JsonContainerContract
    {
        /// <summary>
        /// Gets the <see cref="System.Type"/> of the collection items.
        /// </summary>
        /// <value>The <see cref="System.Type"/> of the collection items.</value>
        public Type? CollectionItemType { get; }

        /// <summary>
        /// Gets a value indicating whether the collection type is a multidimensional array.
        /// </summary>
        /// <value><c>true</c> if the collection type is a multidimensional array; otherwise, <c>false</c>.</value>
        public bool IsMultidimensionalArray { get; }

        private readonly Type? _genericCollectionDefinitionType;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        private Type? _genericWrapperType;
        private ObjectConstructor<object>? _genericWrapperCreator;
        private Func<object>? _genericTemporaryCollectionCreator;

        internal bool IsArray { get; }
        internal bool ShouldCreateWrapper { get; }
        internal bool CanDeserialize { get; private set; }

        private readonly ConstructorInfo? _parameterizedConstructor;

        private ObjectConstructor<object>? _parameterizedCreator;
        private ObjectConstructor<object>? _overrideCreator;

        internal ObjectConstructor<object>? ParameterizedCreator
        {
            [RequiresDynamicCode(MiscellaneousUtils.AotWarning)]
            get
            {
                if (_parameterizedCreator == null && _parameterizedConstructor != null)
                {
                    _parameterizedCreator = JsonTypeReflector.ReflectionDelegateFactory.CreateParameterizedConstructor(_parameterizedConstructor);
                }

                return _parameterizedCreator;
            }
        }

        /// <summary>
        /// Gets or sets the function used to create the object. When set this function will override <see cref="JsonContract.DefaultCreator"/>.
        /// </summary>
        /// <value>The function used to create the object.</value>
        public ObjectConstructor<object>? OverrideCreator
        {
            get => _overrideCreator;
            set
            {
                _overrideCreator = value;
                // hacky
                CanDeserialize = true;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the creator has a parameter with the collection values.
        /// </summary>
        /// <value><c>true</c> if the creator has a parameter with the collection values; otherwise, <c>false</c>.</value>
        public bool HasParameterizedCreator { get; set; }

        internal bool HasParameterizedCreatorInternal => (HasParameterizedCreator || _parameterizedCreator != null || _parameterizedConstructor != null);

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonArrayContract"/> class.
        /// </summary>
        /// <param name="underlyingType">The underlying type for the contract.</param>
        [RequiresUnreferencedCode(MiscellaneousUtils.TrimWarning)]
        [RequiresDynamicCode(MiscellaneousUtils.AotWarning)]
        public JsonArrayContract(Type underlyingType)
            : base(underlyingType)
        {
            ContractType = JsonContractType.Array;

            // netcoreapp3.0 uses EmptyPartition for empty enumerable. Treat as an empty array.
            IsArray = CreatedType.IsArray ||
                (NonNullableUnderlyingType.IsGenericType() && NonNullableUnderlyingType.GetGenericTypeDefinition().FullName == "System.Linq.EmptyPartition`1");

            bool canDeserialize;

            Type? tempCollectionType;
            if (IsArray)
            {
                CollectionItemType = ReflectionUtils.GetCollectionItemType(UnderlyingType)!;
                IsReadOnlyOrFixedSize = true;
                _genericCollectionDefinitionType = typeof(List<>).MakeGenericType(CollectionItemType);

                canDeserialize = true;
                IsMultidimensionalArray = (CreatedType.IsArray && UnderlyingType.GetArrayRank() > 1);
            }
            else if (typeof(IList).IsAssignableFrom(NonNullableUnderlyingType))
            {
                if (ReflectionUtils.ImplementsGenericDefinition(NonNullableUnderlyingType, typeof(ICollection<>), out _genericCollectionDefinitionType))
                {
                    CollectionItemType = _genericCollectionDefinitionType.GetGenericArguments()[0];
                }
                else
                {
                    CollectionItemType = ReflectionUtils.GetCollectionItemType(NonNullableUnderlyingType);
                }

                if (NonNullableUnderlyingType == typeof(IList))
                {
                    CreatedType = typeof(List<object>);
                }

                if (CollectionItemType != null)
                {
                    _parameterizedConstructor = CollectionUtils.ResolveEnumerableCollectionConstructor(NonNullableUnderlyingType, CollectionItemType);
                }

                IsReadOnlyOrFixedSize = ReflectionUtils.InheritsGenericDefinition(NonNullableUnderlyingType, typeof(ReadOnlyCollection<>));
                canDeserialize = true;
            }
            else if (ReflectionUtils.ImplementsGenericDefinition(NonNullableUnderlyingType, typeof(ICollection<>), out _genericCollectionDefinitionType))
            {
                CollectionItemType = _genericCollectionDefinitionType.GetGenericArguments()[0];

                if (ReflectionUtils.IsGenericDefinition(NonNullableUnderlyingType, typeof(ICollection<>))
                    || ReflectionUtils.IsGenericDefinition(NonNullableUnderlyingType, typeof(IList<>)))
                {
                    CreatedType = typeof(List<>).MakeGenericType(CollectionItemType);
                }

#if HAVE_ISET
                if (ReflectionUtils.IsGenericDefinition(NonNullableUnderlyingType, typeof(ISet<>)))
                {
                    CreatedType = typeof(HashSet<>).MakeGenericType(CollectionItemType);
                }
#endif

                _parameterizedConstructor = CollectionUtils.ResolveEnumerableCollectionConstructor(NonNullableUnderlyingType, CollectionItemType);
                canDeserialize = true;
                ShouldCreateWrapper = true;
            }
#if HAVE_READ_ONLY_COLLECTIONS
            else if (ReflectionUtils.ImplementsGenericDefinition(NonNullableUnderlyingType, typeof(IReadOnlyCollection<>), out tempCollectionType))
            {
                CollectionItemType = tempCollectionType.GetGenericArguments()[0];

                if (ReflectionUtils.IsGenericDefinition(NonNullableUnderlyingType, typeof(IReadOnlyCollection<>))
                    || ReflectionUtils.IsGenericDefinition(NonNullableUnderlyingType, typeof(IReadOnlyList<>)))
                {
                    CreatedType = typeof(ReadOnlyCollection<>).MakeGenericType(CollectionItemType);
                }

                _genericCollectionDefinitionType = typeof(List<>).MakeGenericType(CollectionItemType);
                _parameterizedConstructor = CollectionUtils.ResolveEnumerableCollectionConstructor(CreatedType, CollectionItemType);

#if HAVE_FSHARP_TYPES
                StoreFSharpListCreatorIfNecessary(NonNullableUnderlyingType);
#endif

                IsReadOnlyOrFixedSize = true;
                canDeserialize = HasParameterizedCreatorInternal;
            }
#endif
            else if (ReflectionUtils.ImplementsGenericDefinition(NonNullableUnderlyingType, typeof(IEnumerable<>), out tempCollectionType))
            {
                CollectionItemType = tempCollectionType.GetGenericArguments()[0];

                if (ReflectionUtils.IsGenericDefinition(UnderlyingType, typeof(IEnumerable<>)))
                {
                    CreatedType = typeof(List<>).MakeGenericType(CollectionItemType);
                }

                _parameterizedConstructor = CollectionUtils.ResolveEnumerableCollectionConstructor(NonNullableUnderlyingType, CollectionItemType);

#if HAVE_FSHARP_TYPES
                StoreFSharpListCreatorIfNecessary(NonNullableUnderlyingType);
#endif

                if (NonNullableUnderlyingType.IsGenericType() && NonNullableUnderlyingType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    _genericCollectionDefinitionType = tempCollectionType;

                    IsReadOnlyOrFixedSize = false;
                    ShouldCreateWrapper = false;
                    canDeserialize = true;
                }
                else
                {
                    _genericCollectionDefinitionType = typeof(List<>).MakeGenericType(CollectionItemType);

                    IsReadOnlyOrFixedSize = true;
                    ShouldCreateWrapper = true;
                    canDeserialize = HasParameterizedCreatorInternal;
                }
            }
            else
            {
                // types that implement IEnumerable and nothing else
                canDeserialize = false;
                ShouldCreateWrapper = true;
            }

            CanDeserialize = canDeserialize;

#if (NET20 || NET35)
            if (CollectionItemType != null && ReflectionUtils.IsNullableType(CollectionItemType))
            {
                // bug in .NET 2.0 & 3.5 that List<Nullable<T>> throws an error when adding null via IList.Add(object)
                // wrapper will handle calling Add(T) instead
                if (ReflectionUtils.InheritsGenericDefinition(CreatedType, typeof(List<>), out tempCollectionType)
                    || (IsArray && !IsMultidimensionalArray))
                {
                    ShouldCreateWrapper = true;
                }
            }
#endif

            if (CollectionItemType != null &&
                ImmutableCollectionsUtils.TryBuildImmutableForArrayContract(
                NonNullableUnderlyingType,
                CollectionItemType,
                out Type? immutableCreatedType,
                out ObjectConstructor<object>? immutableParameterizedCreator))
            {
                CreatedType = immutableCreatedType;
                _parameterizedCreator = immutableParameterizedCreator;
                IsReadOnlyOrFixedSize = true;
                CanDeserialize = true;
            }
        }

        [RequiresDynamicCode(MiscellaneousUtils.AotWarning)]
        internal IWrappedCollection CreateWrapper(object list)
        {
            if (_genericWrapperCreator == null)
            {
                MiscellaneousUtils.Assert(_genericCollectionDefinitionType != null);
                MiscellaneousUtils.Assert(CollectionItemType != null);

                _genericWrapperType = typeof(CollectionWrapper<>).MakeGenericType(CollectionItemType);

                Type constructorArgument;

                if (ReflectionUtils.InheritsGenericDefinition(_genericCollectionDefinitionType, typeof(List<>))
                    || _genericCollectionDefinitionType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    constructorArgument = typeof(ICollection<>).MakeGenericType(CollectionItemType);
                }
                else
                {
                    constructorArgument = _genericCollectionDefinitionType;
                }

                ConstructorInfo genericWrapperConstructor = _genericWrapperType.GetConstructor(new[] { constructorArgument })!;
                _genericWrapperCreator = JsonTypeReflector.ReflectionDelegateFactory.CreateParameterizedConstructor(genericWrapperConstructor);
            }

            return (IWrappedCollection)_genericWrapperCreator(list);
        }

        [RequiresDynamicCode(MiscellaneousUtils.AotWarning)]
        internal IList CreateTemporaryCollection()
        {
            if (_genericTemporaryCollectionCreator == null)
            {
                // multidimensional array will also have array instances in it
                Type collectionItemType = (IsMultidimensionalArray || CollectionItemType == null)
                    ? typeof(object)
                    : CollectionItemType;

                Type temporaryListType = typeof(List<>).MakeGenericType(collectionItemType);
                _genericTemporaryCollectionCreator = JsonTypeReflector.ReflectionDelegateFactory.CreateDefaultConstructor<object>(temporaryListType);
            }

            return (IList)_genericTemporaryCollectionCreator();
        }

#if HAVE_FSHARP_TYPES
        [RequiresUnreferencedCode(MiscellaneousUtils.TrimWarning)]
        [RequiresDynamicCode(MiscellaneousUtils.AotWarning)]
        private void StoreFSharpListCreatorIfNecessary(Type underlyingType)
        {
            if (!HasParameterizedCreatorInternal && underlyingType.Name == FSharpUtils.FSharpListTypeName)
            {
                FSharpUtils.EnsureInitialized(underlyingType.Assembly());
                _parameterizedCreator = FSharpUtils.Instance.CreateSeq(CollectionItemType!);
            }
        }
#endif
    }
}