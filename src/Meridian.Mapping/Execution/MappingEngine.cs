using System.Collections;
using System.Reflection;
using Meridian.Mapping.Configuration;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Core mapping execution engine. Takes compiled <see cref="TypeMap"/> definitions
/// and executes them at runtime. Handles property mapping, collection mapping,
/// type converters, value resolvers, and recursive mapping with depth tracking.
/// </summary>
public class MappingEngine
{
    private readonly IConfigurationProvider _configurationProvider;
    private readonly ValueTransformerCollection? _valueTransformers;

    /// <summary>
    /// Initializes a new <see cref="MappingEngine"/>.
    /// </summary>
    /// <param name="configurationProvider">The configuration provider holding all type maps.</param>
    /// <param name="valueTransformers">Optional global value transformers.</param>
    public MappingEngine(IConfigurationProvider configurationProvider, ValueTransformerCollection? valueTransformers = null)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _valueTransformers = valueTransformers?.HasTransformers == true ? valueTransformers : null;
    }

    /// <summary>
    /// Maps the source object to the destination type.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="sourceType">The source type.</param>
    /// <param name="destinationType">The destination type.</param>
    /// <param name="context">The resolution context.</param>
    /// <returns>The mapped destination object.</returns>
    public object? Map(object? source, Type sourceType, Type destinationType, ResolutionContext context)
    {
        if (source == null)
        {
            return CreateDefaultValue(destinationType, allowNullOverride: null);
        }

        // Check for collection mapping
        if (IsCollectionMapping(sourceType, destinationType, out var srcElementType, out var destElementType))
        {
            return MapCollection(source, sourceType, destinationType, srcElementType!, destElementType!, context);
        }

        var typeMap = ResolveTypeMap(source, sourceType, destinationType);

        if (typeMap == null)
        {
            sourceType = source.GetType();

            // Handle assignable types (same type, base type, etc.)
            if (destinationType.IsAssignableFrom(sourceType))
                return source;

            // Handle enum conversions
            if (sourceType.IsEnum && destinationType.IsEnum)
                return Enum.ToObject(destinationType, source);

            // Handle primitive/convertible types
            if (IsConvertible(sourceType) && IsConvertible(destinationType))
                return Convert.ChangeType(source, destinationType);

            // Handle nullable target
            var underlyingDest = Nullable.GetUnderlyingType(destinationType);
            if (underlyingDest != null)
                return Map(source, sourceType, underlyingDest, context);

            throw new InvalidOperationException(
                $"Missing mapping configuration for {sourceType.Name} -> {destinationType.Name}. " +
                $"Create a mapping using CreateMap<{sourceType.Name}, {destinationType.Name}>().");
        }

        return MapWithTypeMap(source, typeMap, context);
    }

    /// <summary>
    /// Maps source to an existing destination object (update mapping).
    /// </summary>
    public object MapToExisting(object source, object destination, Type sourceType, Type destinationType, ResolutionContext context)
    {
        if (IsCollectionMapping(sourceType, destinationType, out var srcElementType, out var destElementType))
        {
            var reusedCollection = TryMapCollectionOntoExisting(
                source,
                destination,
                sourceType,
                destinationType,
                srcElementType!,
                destElementType!,
                context);

            if (reusedCollection != null)
                return reusedCollection;
        }

        var typeMap = ResolveTypeMap(source, sourceType, destinationType)
            ?? throw new InvalidOperationException(
                $"Missing mapping configuration for {sourceType.Name} -> {destinationType.Name}.");

        return MapProperties(source, destination, typeMap, context);
    }

    private TypeMap? ResolveTypeMap(object source, Type declaredSourceType, Type destinationType)
    {
        var runtimeSourceType = source.GetType();
        var typeMap = _configurationProvider.FindTypeMap(runtimeSourceType, destinationType);

        if (typeMap == null)
        {
            var baseTypeMap = _configurationProvider.FindTypeMap(declaredSourceType, destinationType);
            if (baseTypeMap != null)
            {
                typeMap = SelectMostSpecificDerivedTypeMap(baseTypeMap, runtimeSourceType, destinationType) ?? baseTypeMap;
            }
        }

        if (typeMap != null)
        {
            typeMap = SelectMostSpecificDerivedTypeMap(typeMap, runtimeSourceType, destinationType) ?? typeMap;
        }

        return typeMap;
    }

    private object? CreateDefaultValue(Type destinationType, bool? allowNullOverride)
    {
        if (!destinationType.IsValueType && IsCollectionType(destinationType, out var destElemType))
        {
            if (allowNullOverride ?? _configurationProvider.AllowNullCollections)
                return null;

            return CreateEmptyCollection(destinationType, destElemType!);
        }

        if (!destinationType.IsValueType && (allowNullOverride ?? true))
            return null;

        return destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null;
    }

    private object? MapWithTypeMap(object source, TypeMap typeMap, ResolutionContext context)
    {
        // Check max depth — per-map override wins, otherwise fall back to the
        // global default (default 64, matching System.Text.Json / Newtonsoft v13+).
        // Hitting the cap returns default(TDestination) instead of throwing,
        // mirroring AutoMapper's MaxDepth semantics. The only difference vs
        // AutoMapper v14 is that the cap is ALWAYS active — no opt-in required.
        var effectiveMaxDepth = typeMap.MaxDepth ?? _configurationProvider.DefaultMaxDepth;
        if (context.Depth >= effectiveMaxDepth)
            return typeMap.DestinationType.IsValueType ? Activator.CreateInstance(typeMap.DestinationType) : null;

        // Fast path: if the TypeMap uses only the simple ForMember+MapFrom
        // subset, a single compiled Func<> replaces the per-property
        // interpreter. FastPathCompiler guarantees behavioural equivalence
        // with the interpreter for the shapes it accepts. The engine + context
        // are passed through so nested calls respect DefaultMaxDepth and
        // PreserveReferences identity.
        if (typeMap.CompiledFastPath is { } fast)
            return fast(source, this, context);

        // PreserveReferences: check if we have already mapped this source object
        if (typeMap.PreserveReferences && context.TryGetMapped(source, typeMap.DestinationType, out var cached))
            return cached;

        // Custom converter (Func) — compiled into Func<object, object?> wrapper
        if (typeMap.CustomConverter != null)
            return typeMap.CustomConverter(source);

        // Type converter instance — pre-compiled Convert method invocation
        if (typeMap.CompiledTypeConverter != null)
        {
            var defaultDest = typeMap.DestinationType.IsValueType
                ? Activator.CreateInstance(typeMap.DestinationType)
                : null;
            return typeMap.CompiledTypeConverter(source, defaultDest, context);
        }

        // Type converter type (DI). Method lookup is cached.
        if (typeMap.TypeConverterType != null)
        {
            var converter = ResolveService(typeMap.TypeConverterType, context);
            var convertMethod = MethodLookupCache.GetInvocableMethod(typeMap.TypeConverterType, "Convert")
                ?? throw new InvalidOperationException(
                    $"Type converter '{typeMap.TypeConverterType.Name}' does not expose a Convert method.");
            var defaultDest = typeMap.DestinationType.IsValueType
                ? Activator.CreateInstance(typeMap.DestinationType)
                : null;
            return convertMethod.Invoke(converter, [source, defaultDest, context]);
        }

        // Create destination
        object destination;
        if (typeMap.ConstructUsing != null)
        {
            destination = typeMap.ConstructUsing(source);
        }
        else if (typeMap.CtorParamMappings != null && typeMap.CtorParamMappings.Count > 0)
        {
            destination = ObjectCreator.CreateWithConstructorMapping(
                typeMap.DestinationType, source, typeMap.SourceType, typeMap.CtorParamMappings);
        }
        else if (typeMap.CompiledObjectFactory != null)
        {
            destination = typeMap.CompiledObjectFactory();
        }
        else
        {
            // Check if destination has a parameterless constructor
            var ctor = typeMap.DestinationType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (ctor != null)
            {
                destination = ObjectCreator.CreateInstance(typeMap.DestinationType);
            }
            else
            {
                // Try constructor mapping from source
                destination = ObjectCreator.CreateWithConstructorMapping(
                    typeMap.DestinationType, source, typeMap.SourceType, null);
            }
        }

        // PreserveReferences: cache before mapping properties so circular refs can find it
        if (typeMap.PreserveReferences)
            context.CacheMapped(source, typeMap.DestinationType, destination);

        return MapProperties(source, destination, typeMap, context);
    }

    private object MapProperties(object source, object destination, TypeMap typeMap, ResolutionContext context)
    {
        var childContext = context.IncrementDepth();

        // Apply base type maps first (Include/IncludeBase inheritance semantics).
        if (typeMap.BaseTypeMaps != null && typeMap.BaseTypeMaps.Count > 0)
        {
            foreach (var baseTypeMap in typeMap.BaseTypeMaps)
            {
                MapProperties(source, destination, baseTypeMap, context);
            }
        }

        // Execute BeforeMap actions
        foreach (var beforeAction in typeMap.BeforeMapActions)
        {
            beforeAction(source, destination, context);
        }

        foreach (var propertyMap in typeMap.PropertyMaps)
        {
            if (propertyMap.Ignored)
                continue;

            // Pre-condition — typed wrapper avoids DynamicInvoke reflection cost.
            if (propertyMap.PreCondition != null)
            {
                var preResult = DelegateCompiler.WrapPredicate(propertyMap.PreCondition)(source);
                if (!preResult) continue;
            }

            // Resolve value
            object? value;

            if (propertyMap.HasConstantValue)
            {
                value = propertyMap.ConstantValue;
            }
            else if (propertyMap.MemberConverterSourceGetter != null)
            {
                var sourceMember = propertyMap.MemberConverterSourceGetter(source);

                if (propertyMap.MemberConverterInstance != null)
                {
                    var converterType = propertyMap.MemberConverterInstance.GetType();
                    var convertMethod = MethodLookupCache.GetInvocableMethod(converterType, "Convert")
                        ?? throw new InvalidOperationException(
                            $"Member converter '{converterType.Name}' does not expose a Convert method.");

                    value = convertMethod.Invoke(propertyMap.MemberConverterInstance, [sourceMember, context]);
                }
                else if (propertyMap.MemberConverterFunc != null)
                {
                    value = DelegateCompiler.WrapFunc1(propertyMap.MemberConverterFunc)(sourceMember!);
                }
                else
                {
                    value = sourceMember;
                }
            }
            else if (propertyMap.ValueResolverType != null)
            {
                value = ResolveWithValueResolver(source, destination, propertyMap, context);
            }
            else if (propertyMap.MemberValueResolverType != null)
            {
                value = ResolveWithMemberValueResolver(source, destination, propertyMap, context);
            }
            else if (propertyMap.CustomMapFunc != null)
            {
                value = DelegateCompiler.WrapFunc2(propertyMap.CustomMapFunc)(source, destination);
            }
            else if (propertyMap.CompiledGetter != null)
            {
                value = propertyMap.CompiledGetter(source);
            }
            else
            {
                continue; // No mapping source configured
            }

            // Null substitution
            if (value == null && propertyMap.HasNullSubstitute)
                value = propertyMap.NullSubstitute;

            // Condition — typed wrapper.
            if (propertyMap.Condition != null)
            {
                var condResult = DelegateCompiler.WrapPredicate(propertyMap.Condition)(source);
                if (!condResult) continue;
            }

            // 3-arg Condition (src, dest, resolvedMember) — typed wrapper.
            if (propertyMap.Condition3Arg != null)
            {
                var condResult = DelegateCompiler.WrapPredicate3(propertyMap.Condition3Arg)(source, destination, value);
                if (!condResult) continue;
            }

            // Type conversion for non-assignable property types
            var destPropType = propertyMap.DestinationProperty.PropertyType;

            if (value == null && IsCollectionType(destPropType, out var destElemType))
            {
                value = (propertyMap.AllowNull ?? _configurationProvider.AllowNullCollections)
                    ? null
                    : CreateEmptyCollection(destPropType, destElemType!);
            }

            if (value != null && propertyMap.UseDestinationValue == true)
            {
                value = TryMapOntoExistingDestinationValue(value, destination, propertyMap, childContext);
            }

            if (value != null && !destPropType.IsAssignableFrom(value.GetType()))
            {
                value = ConvertValue(value, destPropType, childContext);
            }

            // Apply value transformers
            if (value != null && _valueTransformers != null)
            {
                value = _valueTransformers.Apply(value);
            }

            // AllowNullDestinationValues: when false, skip setting null on reference-type properties
            if (value == null && !(propertyMap.AllowNull ?? _configurationProvider.AllowNullDestinationValues)
                && !destPropType.IsValueType
                && !IsCollectionType(destPropType, out _))
            {
                continue;
            }

            // Set the value
            try
            {
                propertyMap.CompiledSetter?.Invoke(destination, value);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Error mapping property '{propertyMap.DestinationProperty.Name}' " +
                    $"on {typeMap.DestinationType.Name}.", ex);
            }
        }

        // Execute AfterMap actions
        foreach (var afterAction in typeMap.AfterMapActions)
        {
            afterAction(source, destination, context);
        }

        return destination;
    }

    private object? TryMapOntoExistingDestinationValue(
        object value,
        object destination,
        PropertyMap propertyMap,
        ResolutionContext context)
    {
        var existingValue = GetCurrentDestinationValue(destination, propertyMap);
        if (existingValue == null)
            return value;

        var destPropType = propertyMap.DestinationProperty.PropertyType;
        var valueType = value.GetType();

        if (IsCollectionMapping(valueType, destPropType, out var srcElementType, out var destElementType))
        {
            var reusedCollection = TryMapCollectionOntoExisting(
                value,
                existingValue,
                valueType,
                destPropType,
                srcElementType!,
                destElementType!,
                context);

            if (reusedCollection != null)
                return reusedCollection;
        }

        var nestedTypeMap = ResolveTypeMap(value, valueType, destPropType);
        if (nestedTypeMap != null)
            return MapProperties(value, existingValue, nestedTypeMap, context);

        return value;
    }

    private object? GetCurrentDestinationValue(object destination, PropertyMap propertyMap)
    {
        if (propertyMap.DestinationPropertyChain == null || propertyMap.DestinationPropertyChain.Length <= 1)
            return propertyMap.DestinationProperty.GetValue(destination);

        object? current = destination;
        for (int i = 0; i < propertyMap.DestinationPropertyChain.Length; i++)
        {
            if (current == null)
                return null;

            current = propertyMap.DestinationPropertyChain[i].GetValue(current);
        }

        return current;
    }

    private object? TryMapCollectionOntoExisting(
        object source,
        object destination,
        Type sourceCollectionType,
        Type destinationCollectionType,
        Type srcElementType,
        Type destElementType,
        ResolutionContext context)
    {
        if (source is not IEnumerable enumerable || destination is not IList destinationList || destination is Array)
            return null;

        EnforceCollectionLimit(source, sourceCollectionType, destinationCollectionType);

        destinationList.Clear();
        var maxItems = _configurationProvider.DefaultMaxCollectionItems;
        var count = 0;
        foreach (var item in enumerable)
        {
            if (++count > maxItems)
            {
                throw new MeridianMappingLimitException(
                    MeridianMappingLimit.MaxCollectionItems,
                    maxItems,
                    count,
                    sourceCollectionType,
                    destinationCollectionType);
            }

            if (item == null)
            {
                destinationList.Add(destElementType.IsValueType ? Activator.CreateInstance(destElementType) : null);
                continue;
            }

            destinationList.Add(Map(item, srcElementType, destElementType, context.IncrementDepth()));
        }

        return destination;
    }

    private object? ConvertValue(object value, Type destType, ResolutionContext context)
    {
        var sourceType = value.GetType();

        // Handle nullable destination
        var underlyingDest = Nullable.GetUnderlyingType(destType);
        if (underlyingDest != null)
            destType = underlyingDest;

        // Direct assignability
        if (destType.IsAssignableFrom(sourceType))
            return value;

        // Collection mapping
        if (IsCollectionMapping(sourceType, destType, out var srcElem, out var destElem))
            return MapCollection(value, sourceType, destType, srcElem!, destElem!, context);

        // Enum
        if (destType.IsEnum)
        {
            if (sourceType == typeof(string))
                return Enum.Parse(destType, (string)value, ignoreCase: true);
            return Enum.ToObject(destType, value);
        }

        if (sourceType.IsEnum && IsConvertible(destType))
            return Convert.ChangeType(value, destType);

        // Try type map
        var typeMap = _configurationProvider.FindTypeMap(sourceType, destType);
        if (typeMap != null)
            return MapWithTypeMap(value, typeMap, context);

        // IConvertible
        if (IsConvertible(sourceType) && IsConvertible(destType))
            return Convert.ChangeType(value, destType);

        // String conversion
        if (destType == typeof(string))
            return value.ToString();

        return value;
    }

    private object? MapCollection(object source, Type sourceType, Type destType,
        Type srcElementType, Type destElementType, ResolutionContext context)
    {
        if (source is not IEnumerable enumerable)
        {
            // When AllowNullCollections is true, return null for null/non-enumerable sources.
            // When false, return an empty collection.
            if (_configurationProvider.AllowNullCollections)
                return null;

            return CreateEmptyCollection(destType, destElementType);
        }

        EnforceCollectionLimit(source, sourceType, destType);

        var items = new List<object?>();
        var maxItems = _configurationProvider.DefaultMaxCollectionItems;
        var count = 0;
        foreach (var item in enumerable)
        {
            if (++count > maxItems)
            {
                throw new MeridianMappingLimitException(
                    MeridianMappingLimit.MaxCollectionItems,
                    maxItems,
                    count,
                    sourceType,
                    destType);
            }

            if (item == null)
            {
                items.Add(destElementType.IsValueType ? Activator.CreateInstance(destElementType) : null);
            }
            else
            {
                items.Add(Map(item, srcElementType, destElementType, context.IncrementDepth()));
            }
        }

        // Create appropriate collection type
        if (destType.IsArray)
        {
            var array = Array.CreateInstance(destElementType, items.Count);
            for (int i = 0; i < items.Count; i++)
                array.SetValue(items[i], i);
            return array;
        }

        // HashSet<T> or ISet<T>
        if (destType.IsGenericType)
        {
            var genericDef = destType.GetGenericTypeDefinition();

            if (genericDef == typeof(HashSet<>) || genericDef == typeof(ISet<>))
            {
                var elemType = destType.GetGenericArguments()[0];
                var hashSetType = typeof(HashSet<>).MakeGenericType(elemType);
                var hashSet = Activator.CreateInstance(hashSetType)!;
                var addMethod = hashSetType.GetMethod("Add")!;
                foreach (var item in items)
                    addMethod.Invoke(hashSet, [item]);
                return hashSet;
            }

            if (genericDef == typeof(Dictionary<,>) ||
                genericDef == typeof(IDictionary<,>) ||
                genericDef == typeof(IReadOnlyDictionary<,>))
            {
                var args = destType.GetGenericArguments();
                var dictType = typeof(Dictionary<,>).MakeGenericType(args);
                var dict = Activator.CreateInstance(dictType)!;
                var keyProp = destElementType.GetProperty("Key")!;
                var valueProp = destElementType.GetProperty("Value")!;
                var indexer = dictType.GetProperty("Item")!;
                foreach (var item in items)
                {
                    if (item != null)
                    {
                        var key = keyProp.GetValue(item);
                        var val = valueProp.GetValue(item);
                        indexer.SetValue(dict, val, [key!]);
                    }
                }
                return dict;
            }
        }

        // List<T>, IList<T>, IEnumerable<T>, ICollection<T>
        var listType = typeof(List<>).MakeGenericType(destElementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in items)
            list.Add(item);

        if (destType.IsAssignableFrom(listType))
            return list;

        // Try to find a constructor that accepts IEnumerable<T>
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(destElementType);
        var ctor = destType.GetConstructor([enumerableType]);
        if (ctor != null)
            return ctor.Invoke([list]);

        return list;
    }

    private object? ResolveWithValueResolver(object source, object destination, PropertyMap propertyMap, ResolutionContext context)
    {
        var resolver = ResolveService(propertyMap.ValueResolverType!, context);
        var resolveMethod = MethodLookupCache.GetInvocableMethod(propertyMap.ValueResolverType!, "Resolve")
            ?? throw new InvalidOperationException(
                $"Value resolver type '{propertyMap.ValueResolverType!.Name}' does not implement a Resolve method.");

        var destDefault = propertyMap.DestinationProperty.PropertyType.IsValueType
            ? Activator.CreateInstance(propertyMap.DestinationProperty.PropertyType)
            : null;

        return resolveMethod.Invoke(resolver, [source, destination, destDefault, context]);
    }

    private object? ResolveWithMemberValueResolver(object source, object destination, PropertyMap propertyMap, ResolutionContext context)
    {
        var resolver = ResolveService(propertyMap.MemberValueResolverType!, context);
        var resolveMethod = MethodLookupCache.GetInvocableMethod(propertyMap.MemberValueResolverType!, "Resolve")
            ?? throw new InvalidOperationException(
                $"Member value resolver type '{propertyMap.MemberValueResolverType!.Name}' does not implement a Resolve method.");

        // Get source member value via the compiled getter
        var sourceMember = propertyMap.MemberValueResolverSourceGetter?.Invoke(source);

        // Get current destination member value
        var destMember = GetCurrentDestinationValue(destination, propertyMap)
            ?? (propertyMap.DestinationProperty.PropertyType.IsValueType
                ? Activator.CreateInstance(propertyMap.DestinationProperty.PropertyType)
                : null);

        return resolveMethod.Invoke(resolver, [source, destination, sourceMember, destMember, context]);
    }

    private object ResolveService(Type serviceType, ResolutionContext context)
    {
        if (context.ServiceProvider != null)
        {
            var service = context.ServiceProvider.GetService(serviceType);
            if (service != null) return service;
        }

        // Fallback: create instance directly
        return Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException(
                $"Could not create instance of '{serviceType.Name}'. " +
                "Register it with the DI container or ensure it has a parameterless constructor.");
    }

    private void EnforceCollectionLimit(object source, Type sourceType, Type destType)
    {
        var maxItems = _configurationProvider.DefaultMaxCollectionItems;

        // Fast path: ICollection/ICollection<T>/array expose Count cheaply,
        // so we can reject oversized input before touching a single element.
        // We check both the non-generic and the generic interfaces because a
        // type can implement one without the other (e.g. custom collection
        // classes that only expose ICollection<T>).
        var knownCount = GetKnownCount(source);
        if (knownCount is int fastCount && fastCount > maxItems)
        {
            throw new MeridianMappingLimitException(
                MeridianMappingLimit.MaxCollectionItems,
                maxItems,
                fastCount,
                sourceType,
                destType);
        }
    }

    private static bool IsCollectionMapping(Type sourceType, Type destType,
        out Type? srcElementType, out Type? destElementType)
    {
        srcElementType = null;
        destElementType = null;

        // Strings implement IEnumerable<char> but should never be treated as collections
        if (sourceType == typeof(string) || destType == typeof(string))
            return false;

        var srcElem = GetElementType(sourceType);
        var destElem = GetElementType(destType);

        if (srcElem != null && destElem != null)
        {
            srcElementType = srcElem;
            destElementType = destElem;
            return true;
        }

        return false;
    }

    private static Type? GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();

            // Single-element collections
            if (genericDef == typeof(IEnumerable<>) ||
                genericDef == typeof(ICollection<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(List<>) ||
                genericDef == typeof(IReadOnlyList<>) ||
                genericDef == typeof(IReadOnlyCollection<>) ||
                genericDef == typeof(HashSet<>) ||
                genericDef == typeof(ISet<>))
            {
                return type.GetGenericArguments()[0];
            }

            // Key-value pair collections (element type is KeyValuePair<K,V>)
            if (genericDef == typeof(Dictionary<,>) ||
                genericDef == typeof(IDictionary<,>) ||
                genericDef == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                return typeof(KeyValuePair<,>).MakeGenericType(args);
            }
        }

        // Check if type implements IEnumerable<T>
        var enumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumInterface?.GetGenericArguments()[0];
    }

    private static bool IsConvertible(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return typeof(IConvertible).IsAssignableFrom(type);
    }

    /// <summary>
    /// Returns the item count of <paramref name="source"/> without touching
    /// its enumerator, if the type exposes one. Checks the non-generic
    /// <see cref="System.Collections.ICollection"/> first (covers arrays,
    /// <see cref="List{T}"/>, <see cref="Dictionary{TKey,TValue}"/>, and
    /// most BCL collections), then falls back to <c>ICollection&lt;T&gt;.Count</c>
    /// via reflection for custom collections that only implement the
    /// generic interface. Returns <c>null</c> when the count is not
    /// cheaply available.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _genericCountPropertyCache = new();

    private static int? GetKnownCount(object source)
    {
        if (source is System.Collections.ICollection nonGeneric)
            return nonGeneric.Count;

        var countProp = _genericCountPropertyCache.GetOrAdd(source.GetType(), static t =>
        {
            var iface = t.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));
            return iface?.GetProperty("Count");
        });

        return countProp is null ? null : (int?)countProp.GetValue(source);
    }

    /// <summary>
    /// Checks if a type is a collection type (without needing a source type).
    /// Used for null-source handling with AllowNullCollections.
    /// </summary>
    private static bool IsCollectionType(Type type, out Type? elementType)
    {
        elementType = null;
        if (type == typeof(string)) return false;
        elementType = GetElementType(type);
        return elementType != null;
    }

    /// <summary>
    /// Creates an empty collection of the given destination type.
    /// </summary>
    private static object CreateEmptyCollection(Type destType, Type destElementType)
    {
        if (destType.IsArray)
        {
            return Array.CreateInstance(destElementType, 0);
        }

        // HashSet / ISet
        if (destType.IsGenericType)
        {
            var genericDef = destType.GetGenericTypeDefinition();

            if (genericDef == typeof(HashSet<>) || genericDef == typeof(ISet<>))
            {
                var hashSetType = typeof(HashSet<>).MakeGenericType(destElementType);
                return Activator.CreateInstance(hashSetType)!;
            }

            if (genericDef == typeof(Dictionary<,>) ||
                genericDef == typeof(IDictionary<,>) ||
                genericDef == typeof(IReadOnlyDictionary<,>))
            {
                var args = destType.GetGenericArguments();
                var dictType = typeof(Dictionary<,>).MakeGenericType(args);
                return Activator.CreateInstance(dictType)!;
            }
        }

        var listType = typeof(List<>).MakeGenericType(destElementType);
        var list = Activator.CreateInstance(listType)!;

        if (destType.IsAssignableFrom(listType))
            return list;

        // Try constructor with IEnumerable<T>
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(destElementType);
        var ctor = destType.GetConstructor([enumerableType]);
        if (ctor != null)
            return ctor.Invoke([list]);

        return list;
    }

    /// <summary>
    /// Selects the most specific derived type map for runtime polymorphic dispatch.
    /// Returns null if no suitable derived map exists.
    /// </summary>
    private static TypeMap? SelectMostSpecificDerivedTypeMap(TypeMap baseTypeMap, Type runtimeSourceType, Type requestedDestinationType)
    {
        if (baseTypeMap.DerivedTypeMaps == null || baseTypeMap.DerivedTypeMaps.Count == 0)
            return null;

        TypeMap? best = null;
        var bestDepth = int.MaxValue;

        foreach (var derived in baseTypeMap.DerivedTypeMaps)
        {
            // runtime source must be assignable to derived source map
            if (!derived.SourceType.IsAssignableFrom(runtimeSourceType))
                continue;

            // derived destination must satisfy requested destination
            if (!requestedDestinationType.IsAssignableFrom(derived.DestinationType))
                continue;

            var depth = GetTypeDistance(runtimeSourceType, derived.SourceType);
            if (depth >= 0 && depth < bestDepth)
            {
                best = derived;
                bestDepth = depth;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns distance in inheritance chain from runtimeType to candidateBaseType.
    /// 0 means exact match, 1 means immediate base, etc. -1 means not assignable.
    /// </summary>
    private static int GetTypeDistance(Type runtimeType, Type candidateBaseType)
    {
        if (!candidateBaseType.IsAssignableFrom(runtimeType))
            return -1;

        if (runtimeType == candidateBaseType)
            return 0;

        var distance = 0;
        var current = runtimeType;
        while (current != null)
        {
            if (current == candidateBaseType)
                return distance;

            current = current.BaseType;
            distance++;
        }

        return -1;
    }
}
