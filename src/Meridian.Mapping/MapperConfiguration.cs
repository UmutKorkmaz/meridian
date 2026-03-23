using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Meridian.Mapping.Configuration;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping;

/// <summary>
/// Holds all compiled mapping configuration. Create once at application startup
/// and register as a singleton. Provides <see cref="IConfigurationProvider"/>
/// and creates <see cref="IMapper"/> instances.
/// </summary>
public class MapperConfiguration : IConfigurationProvider
{
    /// <summary>
    /// Frozen dictionary for all statically-compiled type maps. ~40% faster lookups
    /// than ConcurrentDictionary for read-only workloads.
    /// </summary>
    private readonly FrozenDictionary<(Type, Type), TypeMap> _frozenTypeMaps;

    /// <summary>
    /// Overflow dictionary for lazily-compiled open generic type maps.
    /// These are compiled on first access and cached for subsequent calls.
    /// </summary>
    private readonly ConcurrentDictionary<(Type, Type), TypeMap> _dynamicTypeMaps = new();

    private readonly Dictionary<(Type, Type), object> _openGenericMappings = new();
    private readonly bool _allowNullCollections;
    private readonly bool _allowNullDestinationValues;
    private readonly Configuration.ValueTransformerCollection? _valueTransformers;

    /// <summary>
    /// Creates a new mapper configuration using a configuration action.
    /// </summary>
    /// <param name="configure">Action that configures mappings.</param>
    public MapperConfiguration(Action<IMapperConfigurationExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var expression = new MapperConfigurationExpression();
        configure(expression);

        _allowNullCollections = expression.AllowNullCollections;
        _allowNullDestinationValues = expression.AllowNullDestinationValues;
        _valueTransformers = expression.ValueTransformers.HasTransformers ? expression.ValueTransformers : null;

        CaptureOpenGenericMappings(expression);
        var typeMaps = new Dictionary<(Type, Type), TypeMap>();
        CompileTypeMaps(expression, typeMaps);
        ApplyInheritance(expression, typeMaps);
        BuildPolymorphicDispatch(typeMaps);
        _frozenTypeMaps = typeMaps.ToFrozenDictionary();
    }

    /// <summary>
    /// Creates a new mapper configuration from a pre-built configuration expression.
    /// </summary>
    /// <param name="expression">The configuration expression.</param>
    public MapperConfiguration(MapperConfigurationExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        _allowNullCollections = expression.AllowNullCollections;
        _allowNullDestinationValues = expression.AllowNullDestinationValues;
        _valueTransformers = expression.ValueTransformers.HasTransformers ? expression.ValueTransformers : null;

        CaptureOpenGenericMappings(expression);
        var typeMaps = new Dictionary<(Type, Type), TypeMap>();
        CompileTypeMaps(expression, typeMaps);
        ApplyInheritance(expression, typeMaps);
        BuildPolymorphicDispatch(typeMaps);
        _frozenTypeMaps = typeMaps.ToFrozenDictionary();
    }

    /// <inheritdoc />
    public TypeMap? FindTypeMap(Type sourceType, Type destinationType)
    {
        // Check frozen (static) maps first — ~40% faster than ConcurrentDictionary
        if (_frozenTypeMaps.TryGetValue((sourceType, destinationType), out var typeMap))
            return typeMap;

        // Check dynamic (lazily-compiled open generic) maps
        if (_dynamicTypeMaps.TryGetValue((sourceType, destinationType), out typeMap))
            return typeMap;

        // Check for open generic mapping
        if (sourceType.IsGenericType && destinationType.IsGenericType)
        {
            var srcGenericDef = sourceType.GetGenericTypeDefinition();
            var destGenericDef = destinationType.GetGenericTypeDefinition();

            if (_openGenericMappings.ContainsKey((srcGenericDef, destGenericDef)))
            {
                // Compile on-demand for this closed generic pair
                var closedTypeMap = CompileOpenGenericTypeMap(sourceType, destinationType);
                _dynamicTypeMaps[(sourceType, destinationType)] = closedTypeMap;
                return closedTypeMap;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IMapper CreateMapper()
    {
        return new Mapper(this, _valueTransformers);
    }

    /// <inheritdoc />
    public IMapper CreateMapper(IServiceProvider serviceProvider)
    {
        return new Mapper(this, serviceProvider, _valueTransformers);
    }

    /// <inheritdoc />
    public void AssertConfigurationIsValid()
    {
        var errors = new StringBuilder();
        var warnings = new StringBuilder();

        // 1. Check for duplicate mappings
        var allMaps = GetAllTypeMaps();
        var duplicateGroups = allMaps
            .GroupBy(m => (m.SourceType, m.DestinationType))
            .Where(g => g.Count() > 1);
        foreach (var group in duplicateGroups)
        {
            errors.AppendLine(
                $"Duplicate mapping: {group.Key.SourceType.Name} → {group.Key.DestinationType.Name} " +
                $"is registered {group.Count()} times.");
        }

        foreach (var typeMap in _frozenTypeMaps.Values)
        {
            // Skip validation for type maps that use custom converters
            // since they bypass member-by-member mapping entirely
            if (typeMap.CustomConverter != null ||
                typeMap.TypeConverterType != null ||
                typeMap.CompiledTypeConverter != null)
            {
                continue;
            }

            // 2. Standard unmapped member validation
            IEnumerable<string> unmapped;

            switch (typeMap.ValidationMemberList)
            {
                case MemberList.Destination:
                    unmapped = typeMap.GetUnmappedDestinationMembers();
                    foreach (var member in unmapped)
                    {
                        errors.AppendLine(
                            $"Unmapped destination member '{member}' on " +
                            $"{typeMap.SourceType.Name} -> {typeMap.DestinationType.Name}");
                    }
                    break;

                case MemberList.Source:
                    unmapped = typeMap.GetUnmappedSourceMembers();
                    foreach (var member in unmapped)
                    {
                        errors.AppendLine(
                            $"Unmapped source member '{member}' on " +
                            $"{typeMap.SourceType.Name} -> {typeMap.DestinationType.Name}");
                    }
                    break;

                case MemberList.None:
                    // Skip validation
                    break;
            }

            // 3. Type mismatch warnings: check if mapped properties have incompatible types
            foreach (var pm in typeMap.PropertyMaps)
            {
                if (pm.Ignored || pm.HasConstantValue || pm.CustomMapExpression != null ||
                    pm.CustomMapFunc != null || pm.ValueResolverType != null ||
                    pm.MemberValueResolverType != null || pm.MemberConverterFunc != null ||
                    pm.MemberConverterInstance != null)
                    continue;

                if (pm.SourceProperty != null)
                {
                    var srcType = pm.SourceProperty.PropertyType;
                    var destType = pm.DestinationProperty.PropertyType;

                    // Check if types are directly assignable or have a configured mapping
                    if (!destType.IsAssignableFrom(srcType) &&
                        !IsBuiltInConvertible(srcType, destType) &&
                        FindTypeMap(srcType, destType) == null)
                    {
                        warnings.AppendLine(
                            $"Type mismatch: {typeMap.SourceType.Name}.{pm.SourceProperty.Name} ({srcType.Name}) → " +
                            $"{typeMap.DestinationType.Name}.{pm.DestinationProperty.Name} ({destType.Name}) — " +
                            $"no explicit conversion or type map configured.");
                    }
                }
            }

            // 4. Missing constructor parameter mappings
            if (typeMap.ConstructUsing == null && typeMap.CompiledObjectFactory == null)
            {
                var destType = typeMap.DestinationType;
                var defaultCtor = destType.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (defaultCtor == null)
                {
                    // Needs constructor mapping — check all ctors
                    var ctors = destType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                    if (ctors.Length > 0)
                    {
                        var bestCtor = ctors.OrderByDescending(c => c.GetParameters().Length).First();
                        var sourceProps = typeMap.SourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Select(p => p.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var param in bestCtor.GetParameters())
                        {
                            var hasMapping = typeMap.CtorParamMappings?.ContainsKey(param.Name!) ?? false;
                            var hasSourceMatch = sourceProps.Contains(param.Name!);

                            if (!hasMapping && !hasSourceMatch && !param.HasDefaultValue)
                            {
                                warnings.AppendLine(
                                    $"Constructor parameter '{param.Name}' on {destType.Name} " +
                                    $"has no corresponding source member or explicit mapping " +
                                    $"(from {typeMap.SourceType.Name} → {destType.Name}).");
                            }
                        }
                    }
                }
            }

            // 5. Circular reference detection (only warn if PreserveReferences is off)
            if (!typeMap.PreserveReferences)
            {
                foreach (var pm in typeMap.PropertyMaps)
                {
                    if (pm.Ignored) continue;

                    var destPropType = pm.DestinationProperty.PropertyType;
                    // Direct self-referencing: A → A where A.Prop is also of type A
                    if (destPropType == typeMap.DestinationType &&
                        (pm.SourceProperty?.PropertyType == typeMap.SourceType))
                    {
                        warnings.AppendLine(
                            $"Potential circular reference: {typeMap.SourceType.Name}.{pm.SourceProperty!.Name} → " +
                            $"{typeMap.DestinationType.Name}.{pm.DestinationProperty.Name} — " +
                            $"consider enabling PreserveReferences() or setting MaxDepth().");
                    }
                }
            }
        }

        var message = new StringBuilder();
        if (errors.Length > 0)
        {
            message.AppendLine("Mapping configuration is invalid. The following errors were found:");
            message.Append(errors);
        }
        if (warnings.Length > 0)
        {
            if (errors.Length > 0)
                message.AppendLine();
            message.AppendLine("Warnings:");
            message.Append(warnings);
        }

        if (errors.Length > 0)
        {
            throw new InvalidOperationException(message.ToString());
        }
    }

    /// <summary>
    /// Checks if a type conversion between two types is supported by the built-in converter.
    /// </summary>
    private static bool IsBuiltInConvertible(Type source, Type dest)
    {
        // Nullable unwrapping
        var srcUnderlying = Nullable.GetUnderlyingType(source) ?? source;
        var destUnderlying = Nullable.GetUnderlyingType(dest) ?? dest;

        if (destUnderlying.IsAssignableFrom(srcUnderlying))
            return true;

        // Enum conversions
        if (srcUnderlying.IsEnum || destUnderlying.IsEnum)
            return true;

        // String conversions are always attempted
        if (destUnderlying == typeof(string) || srcUnderlying == typeof(string))
            return true;

        // Numeric conversions via IConvertible
        if (typeof(IConvertible).IsAssignableFrom(srcUnderlying) &&
            typeof(IConvertible).IsAssignableFrom(destUnderlying))
            return true;

        // Collection type conversions (List → Array, etc.)
        if (IsCollectionType(srcUnderlying) && IsCollectionType(destUnderlying))
            return true;

        return false;
    }

    private static bool IsCollectionType(Type type) =>
        type.IsArray ||
        (type.IsGenericType && (
            type.GetGenericTypeDefinition() == typeof(List<>) ||
            type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
            type.GetGenericTypeDefinition() == typeof(ICollection<>) ||
            type.GetGenericTypeDefinition() == typeof(IList<>) ||
            type.GetGenericTypeDefinition() == typeof(HashSet<>) ||
            type.GetGenericTypeDefinition() == typeof(ISet<>)));

    /// <inheritdoc />
    public IReadOnlyCollection<TypeMap> GetAllTypeMaps()
    {
        // Include both frozen (static) and dynamic (open-generic) type maps.
        var all = new List<TypeMap>(_frozenTypeMaps.Count + _dynamicTypeMaps.Count);
        all.AddRange(_frozenTypeMaps.Values);
        all.AddRange(_dynamicTypeMaps.Values);
        return all.AsReadOnly();
    }

    /// <inheritdoc />
    public bool AllowNullCollections => _allowNullCollections;

    /// <inheritdoc />
    public bool AllowNullDestinationValues => _allowNullDestinationValues;

    /// <inheritdoc />
    public string GetMappingPlan<TSource, TDestination>()
        => GetMappingPlan(typeof(TSource), typeof(TDestination));

    /// <inheritdoc />
    public string GetMappingPlan(Type sourceType, Type destinationType)
    {
        var typeMap = FindTypeMap(sourceType, destinationType);
        if (typeMap == null)
            return $"No mapping configured: {sourceType.Name} → {destinationType.Name}";

        return FormatMappingPlan(typeMap);
    }

    private static string FormatMappingPlan(TypeMap typeMap, int indent = 0)
    {
        var sb = new System.Text.StringBuilder();
        var prefix = new string(' ', indent);

        sb.AppendLine($"{prefix}TypeMap: {typeMap.SourceType.Name} → {typeMap.DestinationType.Name}");

        if (typeMap.CustomConverter != null)
        {
            sb.AppendLine($"{prefix}  [Custom Converter Function]");
            return sb.ToString();
        }
        if (typeMap.CompiledTypeConverter != null)
        {
            sb.AppendLine($"{prefix}  [Type Converter Instance (pre-compiled)]");
            return sb.ToString();
        }
        if (typeMap.TypeConverterType != null)
        {
            sb.AppendLine($"{prefix}  [Type Converter: {typeMap.TypeConverterType.Name} (DI-resolved)]");
            return sb.ToString();
        }

        if (typeMap.MaxDepth.HasValue)
            sb.AppendLine($"{prefix}  MaxDepth: {typeMap.MaxDepth.Value}");
        if (typeMap.PreserveReferences)
            sb.AppendLine($"{prefix}  PreserveReferences: true");
        if (typeMap.ConstructUsing != null)
            sb.AppendLine($"{prefix}  ConstructUsing: [custom factory]");
        if (typeMap.CtorParamMappings is { Count: > 0 })
        {
            sb.AppendLine($"{prefix}  Constructor Parameters:");
            foreach (var (paramName, expr) in typeMap.CtorParamMappings)
                sb.AppendLine($"{prefix}    {paramName} ← {expr.Body}");
        }
        if (typeMap.BeforeMapActions.Count > 0)
            sb.AppendLine($"{prefix}  BeforeMap: {typeMap.BeforeMapActions.Count} action(s)");
        if (typeMap.AfterMapActions.Count > 0)
            sb.AppendLine($"{prefix}  AfterMap: {typeMap.AfterMapActions.Count} action(s)");

        if (typeMap.BaseTypeMaps is { Count: > 0 })
        {
            sb.AppendLine($"{prefix}  Inherits from: {string.Join(", ", typeMap.BaseTypeMaps.Select(b => $"{b.SourceType.Name}→{b.DestinationType.Name}"))}");
        }

        sb.AppendLine($"{prefix}  Property Mappings ({typeMap.PropertyMaps.Count}):");
        foreach (var pm in typeMap.PropertyMaps)
        {
            var dest = pm.DestinationProperty.Name;
            if (pm.Ignored)
            {
                sb.AppendLine($"{prefix}    {dest} ← [Ignored]");
            }
            else if (pm.HasConstantValue)
            {
                sb.AppendLine($"{prefix}    {dest} ← Constant({pm.ConstantValue ?? "null"})");
            }
            else if (pm.CustomMapExpression != null)
            {
                sb.AppendLine($"{prefix}    {dest} ← Expression({pm.CustomMapExpression.Body})");
            }
            else if (pm.CustomMapFunc != null)
            {
                sb.AppendLine($"{prefix}    {dest} ← [Custom Func]");
            }
            else if (pm.ValueResolverType != null)
            {
                sb.AppendLine($"{prefix}    {dest} ← Resolver({pm.ValueResolverType.Name})");
            }
            else if (pm.MemberValueResolverType != null)
            {
                sb.AppendLine($"{prefix}    {dest} ← MemberResolver({pm.MemberValueResolverType.Name})");
            }
            else if (pm.MemberConverterInstance != null || pm.MemberConverterFunc != null)
            {
                sb.AppendLine($"{prefix}    {dest} ← [Member Converter]");
            }
            else if (pm.SourcePropertyChain is { Length: > 0 })
            {
                sb.AppendLine($"{prefix}    {dest} ← {string.Join(".", pm.SourcePropertyChain.Select(p => p.Name))} (flattened)");
            }
            else if (pm.SourceProperty != null)
            {
                var srcName = pm.SourceProperty.Name;
                var typeNote = pm.SourceProperty.PropertyType != pm.DestinationProperty.PropertyType
                    ? $" ({pm.SourceProperty.PropertyType.Name}→{pm.DestinationProperty.PropertyType.Name})"
                    : "";
                sb.AppendLine($"{prefix}    {dest} ← {srcName}{typeNote}");
            }
            else if (pm.CompiledGetter != null)
            {
                sb.AppendLine($"{prefix}    {dest} ← [Compiled Getter]");
            }
            else
            {
                sb.AppendLine($"{prefix}    {dest} ← [Unknown source]");
            }

            // Show conditions
            if (pm.PreCondition != null)
                sb.AppendLine($"{prefix}      PreCondition: [set]");
            if (pm.Condition != null)
                sb.AppendLine($"{prefix}      Condition: [set]");
            if (pm.Condition3Arg != null)
                sb.AppendLine($"{prefix}      Condition3Arg: [set]");
            if (pm.HasNullSubstitute)
                sb.AppendLine($"{prefix}      NullSubstitute: {pm.NullSubstitute ?? "null"}");
        }

        return sb.ToString();
    }

    private void CaptureOpenGenericMappings(MapperConfigurationExpression expression)
    {
        foreach (var (key, value) in expression.OpenGenericMappings)
        {
            _openGenericMappings[key] = value;
        }
    }

    private TypeMap CompileOpenGenericTypeMap(Type closedSourceType, Type closedDestType)
    {
        // Create a default MappingExpression for the closed types (auto-mapping only)
        var exprType = typeof(MappingExpression<,>).MakeGenericType(closedSourceType, closedDestType);
        var expression = Activator.CreateInstance(exprType, (Action<Type, Type, object>?)null)!;
        return CompileTypeMap(closedSourceType, closedDestType, expression);
    }

    private void CompileTypeMaps(MapperConfigurationExpression expression, Dictionary<(Type, Type), TypeMap> typeMaps)
    {
        // First pass: auto-register any Include<> derived pairs that don't already exist
        AutoRegisterIncludedDerivedMaps(expression);

        foreach (var (key, mappingExpr) in expression.MappingExpressions)
        {
            var typeMap = CompileTypeMap(key.Source, key.Dest, mappingExpr);
            typeMaps[(key.Source, key.Dest)] = typeMap;
        }
    }

    /// <summary>
    /// Scans all mapping expressions for Include and IncludeAllDerived
    /// and auto-registers derived maps that don't already exist.
    /// </summary>
    private void AutoRegisterIncludedDerivedMaps(MapperConfigurationExpression expression)
    {
        var toAdd = new List<(Type, Type, object)>();

        foreach (var (key, mappingExpr) in expression.MappingExpressions)
        {
            var exprType = mappingExpr.GetType();

            // Check for explicit Include<> derived pairs
            var includedDerivedProp = exprType.GetProperty("IncludedDerived", BindingFlags.NonPublic | BindingFlags.Instance);
            if (includedDerivedProp?.GetValue(mappingExpr) is System.Collections.IList includedDerived)
            {
                foreach (var pair in includedDerived)
                {
                    var derivedSrcType = (Type)pair.GetType().GetField("Item1")!.GetValue(pair)!;
                    var derivedDestType = (Type)pair.GetType().GetField("Item2")!.GetValue(pair)!;
                    var derivedKey = (derivedSrcType, derivedDestType);

                    if (!expression.MappingExpressions.ContainsKey(derivedKey))
                    {
                        var derivedExprType = typeof(MappingExpression<,>).MakeGenericType(derivedSrcType, derivedDestType);
                        var derivedExpr = Activator.CreateInstance(derivedExprType, (Action<Type, Type, object>?)null)!;
                        toAdd.Add((derivedSrcType, derivedDestType, derivedExpr));
                    }
                }
            }

            // Check for IncludeAllDerived
            var includeAllProp = exprType.GetProperty("IncludeAllDerivedEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
            if (includeAllProp?.GetValue(mappingExpr) is true)
            {
                // Scan existing maps for derived types
                foreach (var (otherKey, _) in expression.MappingExpressions)
                {
                    if (otherKey == key) continue;
                    if (key.Source.IsAssignableFrom(otherKey.Source) && key.Dest.IsAssignableFrom(otherKey.Dest))
                    {
                        // This is a derived map, will be wired up in ApplyInheritance
                    }
                }
            }
        }

        foreach (var (srcType, destType, expr) in toAdd)
        {
            expression.MappingExpressions[(srcType, destType)] = expr;
        }
    }

    /// <summary>
    /// After all type maps are compiled, applies inheritance:
    /// - IncludeBase: copies base config into derived
    /// - Include/IncludeAllDerived: wires derived maps for polymorphic dispatch
    /// </summary>
    private void ApplyInheritance(MapperConfigurationExpression expression, Dictionary<(Type, Type), TypeMap> typeMaps)
    {
        foreach (var (key, mappingExpr) in expression.MappingExpressions)
        {
            var exprType = mappingExpr.GetType();

            // IncludeBase: copy base PropertyMaps into this derived type map
            var includedBasesProp = exprType.GetProperty("IncludedBases", BindingFlags.NonPublic | BindingFlags.Instance);
            if (includedBasesProp?.GetValue(mappingExpr) is System.Collections.IList includedBases)
            {
                foreach (var basePair in includedBases)
                {
                    var baseSrcType = (Type)basePair.GetType().GetField("Item1")!.GetValue(basePair)!;
                    var baseDestType = (Type)basePair.GetType().GetField("Item2")!.GetValue(basePair)!;

                    if (typeMaps.TryGetValue((baseSrcType, baseDestType), out var baseTypeMap) &&
                        typeMaps.TryGetValue((key.Source, key.Dest), out var derivedTypeMap))
                    {
                        InheritPropertyMaps(baseTypeMap, derivedTypeMap);
                        if (!derivedTypeMap.BaseTypeMaps.Contains(baseTypeMap))
                            derivedTypeMap.BaseTypeMaps.Add(baseTypeMap);
                    }
                }
            }

            // Include<>: register derived maps with base inheritance
            var includedDerivedProp = exprType.GetProperty("IncludedDerived", BindingFlags.NonPublic | BindingFlags.Instance);
            if (includedDerivedProp?.GetValue(mappingExpr) is System.Collections.IList includedDerived)
            {
                foreach (var pair in includedDerived)
                {
                    var derivedSrcType = (Type)pair.GetType().GetField("Item1")!.GetValue(pair)!;
                    var derivedDestType = (Type)pair.GetType().GetField("Item2")!.GetValue(pair)!;

                    if (typeMaps.TryGetValue((key.Source, key.Dest), out var baseTypeMap) &&
                        typeMaps.TryGetValue((derivedSrcType, derivedDestType), out var derivedTypeMap))
                    {
                        InheritPropertyMaps(baseTypeMap, derivedTypeMap);
                        if (!derivedTypeMap.BaseTypeMaps.Contains(baseTypeMap))
                            derivedTypeMap.BaseTypeMaps.Add(baseTypeMap);
                    }
                }
            }

            // IncludeAllDerived: scan for all type maps that derive from this one
            var includeAllProp = exprType.GetProperty("IncludeAllDerivedEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
            if (includeAllProp?.GetValue(mappingExpr) is true)
            {
                if (typeMaps.TryGetValue((key.Source, key.Dest), out var baseTypeMap))
                {
                    foreach (var (otherKey, otherTypeMap) in typeMaps)
                    {
                        if (otherKey == (key.Source, key.Dest)) continue;
                        if (key.Source.IsAssignableFrom(otherKey.Item1) && key.Dest.IsAssignableFrom(otherKey.Item2))
                        {
                            InheritPropertyMaps(baseTypeMap, otherTypeMap);
                            if (!otherTypeMap.BaseTypeMaps.Contains(baseTypeMap))
                                otherTypeMap.BaseTypeMaps.Add(baseTypeMap);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Copies property maps from a base type map into a derived type map.
    /// Only copies maps for destination properties that aren't already explicitly configured in the derived map.
    /// </summary>
    private static void InheritPropertyMaps(TypeMap baseMap, TypeMap derivedMap)
    {
        var derivedMappedProps = derivedMap.PropertyMaps
            .Where(pm => pm.IsExplicitlyConfigured)
            .Select(pm => pm.DestinationProperty.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var basePropMap in baseMap.PropertyMaps)
        {
            if (!derivedMappedProps.Contains(basePropMap.DestinationProperty.Name))
            {
                // Find corresponding destination property on derived type
                var derivedDestProp = derivedMap.DestinationType.GetProperty(
                    basePropMap.DestinationProperty.Name,
                    BindingFlags.Public | BindingFlags.Instance);

                if (derivedDestProp != null)
                {
                    // Clone the base property map for the derived type
                    var cloned = new PropertyMap(derivedDestProp)
                    {
                        SourceProperty = basePropMap.SourceProperty,
                        SourcePropertyChain = basePropMap.SourcePropertyChain,
                        DestinationPropertyChain = basePropMap.DestinationPropertyChain,
                        CustomMapExpression = basePropMap.CustomMapExpression,
                        CustomMapFunc = basePropMap.CustomMapFunc,
                        ValueResolverType = basePropMap.ValueResolverType,
                        MemberValueResolverType = basePropMap.MemberValueResolverType,
                        MemberValueResolverSourceGetter = basePropMap.MemberValueResolverSourceGetter,
                        Ignored = basePropMap.Ignored,
                        IsExplicitlyConfigured = basePropMap.IsExplicitlyConfigured,
                        Condition = basePropMap.Condition,
                        Condition3Arg = basePropMap.Condition3Arg,
                        PreCondition = basePropMap.PreCondition,
                        NullSubstitute = basePropMap.NullSubstitute,
                        HasNullSubstitute = basePropMap.HasNullSubstitute,
                        ConstantValue = basePropMap.ConstantValue,
                        HasConstantValue = basePropMap.HasConstantValue,
                        CompiledGetter = basePropMap.CompiledGetter,
                        CompiledSetter = basePropMap.CompiledSetter,
                        MemberConverterFunc = basePropMap.MemberConverterFunc,
                        MemberConverterInstance = basePropMap.MemberConverterInstance,
                        MemberConverterSourceGetter = basePropMap.MemberConverterSourceGetter
                    };
                    derivedMap.PropertyMaps.Add(cloned);
                }
            }
        }

        // Inherit BeforeMap/AfterMap actions
        foreach (var beforeAction in baseMap.BeforeMapActions)
        {
            if (!derivedMap.BeforeMapActions.Contains(beforeAction))
                derivedMap.BeforeMapActions.Add(beforeAction);
        }
        foreach (var afterAction in baseMap.AfterMapActions)
        {
            if (!derivedMap.AfterMapActions.Contains(afterAction))
                derivedMap.AfterMapActions.Add(afterAction);
        }
    }

    /// <summary>
    /// Builds the polymorphic dispatch lookup: for each type map, finds all
    /// derived type maps so the engine can pick the most specific one at runtime.
    /// </summary>
    private void BuildPolymorphicDispatch(Dictionary<(Type, Type), TypeMap> typeMaps)
    {
        foreach (var (key, typeMap) in typeMaps)
        {
            foreach (var (otherKey, otherTypeMap) in typeMaps)
            {
                if (otherKey == key) continue;

                // otherKey is a derived map if its source derives from key's source
                // and its dest derives from key's dest
                if (key.Item1.IsAssignableFrom(otherKey.Item1) &&
                    key.Item2.IsAssignableFrom(otherKey.Item2) &&
                    (otherKey.Item1 != key.Item1 || otherKey.Item2 != key.Item2))
                {
                    typeMap.DerivedTypeMaps.Add(otherTypeMap);
                }
            }
        }
    }

    private TypeMap CompileTypeMap(Type sourceType, Type destType, object mappingExpression)
    {
        var typeMap = new TypeMap(sourceType, destType);

        // Use the ICompiledMappingExpression interface when available (zero-reflection path).
        // Falls back to the original reflection path for open generic compilations
        // where we create a raw MappingExpression via Activator.
        if (mappingExpression is ICompiledMappingExpression compiled)
        {
            return CompileTypeMapFast(sourceType, destType, compiled, typeMap);
        }

        // Fallback: reflection-based compilation for dynamically created expressions
        return CompileTypeMapReflection(sourceType, destType, mappingExpression, typeMap);
    }

    /// <summary>
    /// Fast path: compiles a TypeMap using the ICompiledMappingExpression interface.
    /// No reflection calls — all config is accessed via direct interface method calls.
    /// </summary>
    private TypeMap CompileTypeMapFast(Type sourceType, Type destType, ICompiledMappingExpression expr, TypeMap typeMap)
    {
        // Custom converter
        var customConverter = expr.GetCustomConverter();
        if (customConverter != null)
        {
            typeMap.CustomConverter = WrapToObjectFunc(customConverter);
            return typeMap;
        }

        // Type converter type (DI-resolved)
        var typeConverterType = expr.GetTypeConverterType();
        if (typeConverterType != null)
        {
            typeMap.TypeConverterType = typeConverterType;
            return typeMap;
        }

        // Type converter instance — pre-compile the Convert method call
        var typeConverterInstance = expr.GetTypeConverterInstance();
        if (typeConverterInstance != null)
        {
            typeMap.CompiledTypeConverter = CompileTypeConverterInvocation(typeConverterInstance);
            return typeMap;
        }

        // ConstructUsing
        var constructUsing = expr.GetConstructUsingFunc();
        if (constructUsing != null)
        {
            typeMap.ConstructUsing = WrapToObjectFuncNonNull(constructUsing);
        }

        // Simple properties
        typeMap.MaxDepth = expr.GetMaxDepthValue();
        typeMap.ValidationMemberList = expr.GetValidationMemberList();
        typeMap.PreserveReferences = expr.GetPreserveReferencesEnabled();

        // BeforeMap / AfterMap actions — wrap into Action<object, object>
        foreach (var action in expr.GetBeforeMapActions())
        {
            if (action is Delegate d)
                typeMap.BeforeMapActions.Add(WrapToObjectAction(d));
        }
        foreach (var action in expr.GetAfterMapActions())
        {
            if (action is Delegate d)
                typeMap.AfterMapActions.Add(WrapToObjectAction(d));
        }

        // Constructor parameter configs
        var ctorConfigs = expr.GetCtorParamConfigs();
        if (ctorConfigs.Count > 0)
        {
            typeMap.CtorParamMappings = new Dictionary<string, LambdaExpression>();
            foreach (System.Collections.DictionaryEntry entry in ctorConfigs)
            {
                var paramName = (string)entry.Key;
                if (entry.Value is ICompiledCtorParamConfig ctorConfig)
                {
                    var lambda = ctorConfig.GetMapFromExpression();
                    if (lambda != null)
                        typeMap.CtorParamMappings[paramName] = lambda;
                }
            }
        }

        // Compile object factory
        typeMap.CompiledObjectFactory = ObjectCreator.CompileFactory(destType);

        // IncludeMembers
        var includedMembersList = expr.GetIncludedMemberExpressions();
        List<(Type memberType, Func<object, object?> getter)>? includedMemberInfos = null;
        if (includedMembersList.Count > 0)
        {
            includedMemberInfos = new();
            typeMap.IncludedMemberGetters = new();
            foreach (var memberExpr in includedMembersList)
            {
                if (memberExpr is LambdaExpression lambda)
                {
                    var compiledGetter = CompileIncludedMemberGetter(lambda);
                    typeMap.IncludedMemberGetters.Add(compiledGetter);
                    var bodyType = lambda.Body.Type;
                    if (lambda.Body is UnaryExpression unary &&
                        (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
                    {
                        bodyType = unary.Operand.Type;
                    }
                    includedMemberInfos.Add((bodyType, compiledGetter));
                }
            }
        }

        // Member configs
        var memberConfigs = expr.GetMemberConfigs();
        var hasForAllMembers = expr.GetHasForAllMembers();
        var forAllMembersAction = expr.GetForAllMembersAction();
        var hasForAllOtherMembers = expr.GetHasForAllOtherMembers();
        var forAllOtherMembersAction = expr.GetForAllOtherMembersAction();

        // Build property maps
        var sourceProperties = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        var sourceFields = sourceType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var destProperties = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

        foreach (var destProp in destProperties)
        {
            var propMap = new PropertyMap(destProp);
            ICompiledMemberConfig? memberConfig = null;

            if (memberConfigs.Contains(destProp.Name))
            {
                memberConfig = memberConfigs[destProp.Name] as ICompiledMemberConfig;
            }

            if (memberConfig != null)
            {
                ApplyMemberConfigFast(propMap, memberConfig, sourceType);
            }
            else
            {
                if (sourceProperties.TryGetValue(destProp.Name, out var sourceProp))
                {
                    propMap.SourceProperty = sourceProp;
                }
                else
                {
                    var sourceField = sourceFields.FirstOrDefault(f =>
                        string.Equals(f.Name, destProp.Name, StringComparison.OrdinalIgnoreCase));
                    if (sourceField != null)
                    {
                        propMap.CompiledGetter = CompileFieldGetter(sourceField);
                    }
                    else
                    {
                        bool foundInIncluded = false;
                        if (includedMemberInfos != null)
                        {
                            foreach (var (memberType, getter) in includedMemberInfos)
                            {
                                var includedProp = memberType.GetProperty(destProp.Name,
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                if (includedProp != null && includedProp.CanRead)
                                {
                                    var outerGetter = getter;
                                    var innerProp = includedProp;
                                    propMap.CompiledGetter = src =>
                                    {
                                        var intermediate = outerGetter(src);
                                        return intermediate == null ? null : innerProp.GetValue(intermediate);
                                    };
                                    foundInIncluded = true;
                                    break;
                                }
                            }
                        }

                        if (!foundInIncluded)
                        {
                            var chain = TryFlatten(destProp.Name, sourceType);
                            if (chain != null)
                            {
                                propMap.SourcePropertyChain = chain;
                            }
                            else
                            {
                                continue;
                            }
                        }
                    }
                }
            }

            // Apply ForAllMembers / ForAllOtherMembers
            if (hasForAllMembers && forAllMembersAction != null && memberConfig == null)
            {
                var configType = typeof(MemberConfigurationExpression<,>).MakeGenericType(sourceType, destType);
                var allMembersConfig = (ICompiledMemberConfig)Activator.CreateInstance(configType)!;
                forAllMembersAction.DynamicInvoke(allMembersConfig);
                ApplyForAllMembersConfigFast(propMap, allMembersConfig);
            }

            if (hasForAllOtherMembers && forAllOtherMembersAction != null && memberConfig == null)
            {
                var configType = typeof(MemberConfigurationExpression<,>).MakeGenericType(sourceType, destType);
                var otherMembersConfig = (ICompiledMemberConfig)Activator.CreateInstance(configType)!;
                forAllOtherMembersAction.DynamicInvoke(otherMembersConfig);
                ApplyForAllMembersConfigFast(propMap, otherMembersConfig);
            }

            propMap.Compile();
            typeMap.PropertyMaps.Add(propMap);
        }

        // Process ForPath configs
        var forPathConfigs = expr.GetForPathConfigs();
        if (forPathConfigs.Count > 0)
        {
            foreach (var fpConfig in forPathConfigs)
            {
                var fpType = fpConfig.GetType();
                var chainProp = fpType.GetProperty("DestinationPropertyChain")!;
                var memberConfigProp = fpType.GetProperty("MemberConfig")!;

                var chain = (MemberInfo[])chainProp.GetValue(fpConfig)!;
                var fpMemberConfig = memberConfigProp.GetValue(fpConfig)!;

                var propChain = chain.Cast<PropertyInfo>().ToArray();
                var finalProp = propChain[^1];
                var forPathPropMap = new PropertyMap(finalProp, propChain);

                if (fpMemberConfig is ICompiledMemberConfig compiledFp)
                    ApplyMemberConfigFast(forPathPropMap, compiledFp, sourceType);
                else
                    ApplyMemberConfig(forPathPropMap, fpMemberConfig, sourceType);

                forPathPropMap.Compile();
                typeMap.PropertyMaps.Add(forPathPropMap);
            }
        }

        return typeMap;
    }

    /// <summary>
    /// Reflection fallback for CompileTypeMap (used for open generic on-demand compilation).
    /// </summary>
    private TypeMap CompileTypeMapReflection(Type sourceType, Type destType, object mappingExpression, TypeMap typeMap)
    {
        var exprType = mappingExpression.GetType();

        var customConverterProp = exprType.GetProperty("CustomConverter", BindingFlags.NonPublic | BindingFlags.Instance);
        var customConverter = customConverterProp?.GetValue(mappingExpression) as Delegate;
        if (customConverter != null)
        {
            typeMap.CustomConverter = WrapToObjectFunc(customConverter);
            return typeMap;
        }

        var typeConverterTypeProp = exprType.GetProperty("TypeConverterType", BindingFlags.NonPublic | BindingFlags.Instance);
        var typeConverterType = typeConverterTypeProp?.GetValue(mappingExpression) as Type;
        if (typeConverterType != null)
        {
            typeMap.TypeConverterType = typeConverterType;
            return typeMap;
        }

        var typeConverterInstanceProp = exprType.GetProperty("TypeConverterInstance", BindingFlags.NonPublic | BindingFlags.Instance);
        var typeConverterInstance = typeConverterInstanceProp?.GetValue(mappingExpression);
        if (typeConverterInstance != null)
        {
            typeMap.CompiledTypeConverter = CompileTypeConverterInvocation(typeConverterInstance);
            return typeMap;
        }

        var constructUsingProp = exprType.GetProperty("ConstructUsingFunc", BindingFlags.NonPublic | BindingFlags.Instance);
        var constructUsingDelegate = constructUsingProp?.GetValue(mappingExpression) as Delegate;
        if (constructUsingDelegate != null)
            typeMap.ConstructUsing = WrapToObjectFuncNonNull(constructUsingDelegate);

        var maxDepthProp = exprType.GetProperty("MaxDepthValue", BindingFlags.NonPublic | BindingFlags.Instance);
        typeMap.MaxDepth = maxDepthProp?.GetValue(mappingExpression) as int?;

        var validationProp = exprType.GetProperty("ValidationMemberList", BindingFlags.NonPublic | BindingFlags.Instance);
        if (validationProp?.GetValue(mappingExpression) is MemberList memberList)
            typeMap.ValidationMemberList = memberList;

        var beforeMapProp = exprType.GetProperty("BeforeMapActions", BindingFlags.NonPublic | BindingFlags.Instance);
        if (beforeMapProp?.GetValue(mappingExpression) is System.Collections.IList beforeActions)
        {
            foreach (var action in beforeActions)
            {
                if (action is Delegate d)
                    typeMap.BeforeMapActions.Add(WrapToObjectAction(d));
            }
        }

        var afterMapProp = exprType.GetProperty("AfterMapActions", BindingFlags.NonPublic | BindingFlags.Instance);
        if (afterMapProp?.GetValue(mappingExpression) is System.Collections.IList afterActions)
        {
            foreach (var action in afterActions)
            {
                if (action is Delegate d)
                    typeMap.AfterMapActions.Add(WrapToObjectAction(d));
            }
        }

        var preserveRefsProp = exprType.GetProperty("PreserveReferencesEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
        if (preserveRefsProp?.GetValue(mappingExpression) is bool preserveRefs && preserveRefs)
            typeMap.PreserveReferences = true;

        var ctorConfigsProp = exprType.GetProperty("CtorParamConfigs", BindingFlags.NonPublic | BindingFlags.Instance);
        if (ctorConfigsProp?.GetValue(mappingExpression) is System.Collections.IDictionary ctorConfigs && ctorConfigs.Count > 0)
        {
            typeMap.CtorParamMappings = new Dictionary<string, LambdaExpression>();
            foreach (System.Collections.DictionaryEntry entry in ctorConfigs)
            {
                var paramName = (string)entry.Key;
                if (entry.Value is ICompiledCtorParamConfig ctorConfig)
                {
                    var lambda = ctorConfig.GetMapFromExpression();
                    if (lambda != null)
                        typeMap.CtorParamMappings[paramName] = lambda;
                }
                else
                {
                    var ctorExprType = entry.Value!.GetType();
                    var mapFromProp = ctorExprType.GetProperty("MapFromExpression", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mapFromProp?.GetValue(entry.Value) is LambdaExpression lambda2)
                        typeMap.CtorParamMappings[paramName] = lambda2;
                }
            }
        }

        typeMap.CompiledObjectFactory = ObjectCreator.CompileFactory(destType);

        var includedMembersProp = exprType.GetProperty("IncludedMemberExpressions", BindingFlags.NonPublic | BindingFlags.Instance);
        var includedMembersList = includedMembersProp?.GetValue(mappingExpression) as System.Collections.IList;

        List<(Type memberType, Func<object, object?> getter)>? includedMemberInfos = null;
        if (includedMembersList != null && includedMembersList.Count > 0)
        {
            includedMemberInfos = new();
            typeMap.IncludedMemberGetters = new();
            foreach (var memberExpr in includedMembersList)
            {
                if (memberExpr is LambdaExpression lambda)
                {
                    var compiledGetter = CompileIncludedMemberGetter(lambda);
                    typeMap.IncludedMemberGetters.Add(compiledGetter);
                    var bodyType = lambda.Body.Type;
                    if (lambda.Body is UnaryExpression unary &&
                        (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
                        bodyType = unary.Operand.Type;
                    includedMemberInfos.Add((bodyType, compiledGetter));
                }
            }
        }

        var memberConfigsProp = exprType.GetProperty("MemberConfigs", BindingFlags.NonPublic | BindingFlags.Instance);
        var memberConfigs = memberConfigsProp?.GetValue(mappingExpression) as System.Collections.IDictionary;

        var hasForAllMembersProp = exprType.GetProperty("HasForAllMembers", BindingFlags.NonPublic | BindingFlags.Instance);
        var forAllMembersActionProp = exprType.GetProperty("ForAllMembersAction", BindingFlags.NonPublic | BindingFlags.Instance);
        var hasForAllMembers = (bool)(hasForAllMembersProp?.GetValue(mappingExpression) ?? false);
        var forAllMembersAction = forAllMembersActionProp?.GetValue(mappingExpression) as Delegate;

        var hasForAllOtherMembersProp = exprType.GetProperty("HasForAllOtherMembers", BindingFlags.NonPublic | BindingFlags.Instance);
        var forAllOtherMembersActionProp = exprType.GetProperty("ForAllOtherMembersAction", BindingFlags.NonPublic | BindingFlags.Instance);
        var hasForAllOtherMembers = (bool)(hasForAllOtherMembersProp?.GetValue(mappingExpression) ?? false);
        var forAllOtherMembersAction = forAllOtherMembersActionProp?.GetValue(mappingExpression) as Delegate;

        var sourceProperties = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        var sourceFields = sourceType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var destProperties = destType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

        foreach (var destProp in destProperties)
        {
            var propMap = new PropertyMap(destProp);
            object? memberConfig = null;
            if (memberConfigs != null && memberConfigs.Contains(destProp.Name))
                memberConfig = memberConfigs[destProp.Name];

            if (memberConfig != null)
            {
                if (memberConfig is ICompiledMemberConfig compiledMc)
                    ApplyMemberConfigFast(propMap, compiledMc, sourceType);
                else
                    ApplyMemberConfig(propMap, memberConfig, sourceType);
            }
            else
            {
                if (sourceProperties.TryGetValue(destProp.Name, out var sourceProp))
                {
                    propMap.SourceProperty = sourceProp;
                }
                else
                {
                    var sourceField = sourceFields.FirstOrDefault(f =>
                        string.Equals(f.Name, destProp.Name, StringComparison.OrdinalIgnoreCase));
                    if (sourceField != null)
                    {
                        propMap.CompiledGetter = CompileFieldGetter(sourceField);
                    }
                    else
                    {
                        bool foundInIncluded = false;
                        if (includedMemberInfos != null)
                        {
                            foreach (var (memberType, getter) in includedMemberInfos)
                            {
                                var includedProp = memberType.GetProperty(destProp.Name,
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                if (includedProp != null && includedProp.CanRead)
                                {
                                    var outerGetter = getter;
                                    var innerProp = includedProp;
                                    propMap.CompiledGetter = src =>
                                    {
                                        var intermediate = outerGetter(src);
                                        return intermediate == null ? null : innerProp.GetValue(intermediate);
                                    };
                                    foundInIncluded = true;
                                    break;
                                }
                            }
                        }

                        if (!foundInIncluded)
                        {
                            var chain = TryFlatten(destProp.Name, sourceType);
                            if (chain != null)
                                propMap.SourcePropertyChain = chain;
                            else
                                continue;
                        }
                    }
                }
            }

            if (hasForAllMembers && forAllMembersAction != null && memberConfig == null)
            {
                var configType = typeof(MemberConfigurationExpression<,>).MakeGenericType(sourceType, destType);
                var allMembersConfig = Activator.CreateInstance(configType)!;
                forAllMembersAction.DynamicInvoke(allMembersConfig);
                ApplyForAllMembersConfig(propMap, allMembersConfig);
            }

            if (hasForAllOtherMembers && forAllOtherMembersAction != null && memberConfig == null)
            {
                var configType = typeof(MemberConfigurationExpression<,>).MakeGenericType(sourceType, destType);
                var otherMembersConfig = Activator.CreateInstance(configType)!;
                forAllOtherMembersAction.DynamicInvoke(otherMembersConfig);
                ApplyForAllMembersConfig(propMap, otherMembersConfig);
            }

            propMap.Compile();
            typeMap.PropertyMaps.Add(propMap);
        }

        var forPathConfigsProp = exprType.GetProperty("ForPathConfigs", BindingFlags.NonPublic | BindingFlags.Instance);
        if (forPathConfigsProp?.GetValue(mappingExpression) is System.Collections.IList forPathConfigs && forPathConfigs.Count > 0)
        {
            foreach (var fpConfig in forPathConfigs)
            {
                var fpType = fpConfig.GetType();
                var chainProp = fpType.GetProperty("DestinationPropertyChain")!;
                var memberConfigProp = fpType.GetProperty("MemberConfig")!;
                var chain = (MemberInfo[])chainProp.GetValue(fpConfig)!;
                var fpMemberConfig = memberConfigProp.GetValue(fpConfig)!;
                var propChain = chain.Cast<PropertyInfo>().ToArray();
                var finalProp = propChain[^1];
                var forPathPropMap = new PropertyMap(finalProp, propChain);

                if (fpMemberConfig is ICompiledMemberConfig compiledFp)
                    ApplyMemberConfigFast(forPathPropMap, compiledFp, sourceType);
                else
                    ApplyMemberConfig(forPathPropMap, fpMemberConfig, sourceType);

                forPathPropMap.Compile();
                typeMap.PropertyMaps.Add(forPathPropMap);
            }
        }

        return typeMap;
    }

    /// <summary>
    /// Fast path for applying member config using the ICompiledMemberConfig interface.
    /// </summary>
    private static void ApplyMemberConfigFast(PropertyMap propMap, ICompiledMemberConfig config, Type sourceType)
    {
        propMap.IsExplicitlyConfigured = true;

        if (config.GetIsIgnored())
        {
            propMap.Ignored = true;
            return;
        }

        if (config.GetHasConstantValue())
        {
            propMap.HasConstantValue = true;
            propMap.ConstantValue = config.GetConstantValue();
            return;
        }

        var memberConverterFunc = config.GetMemberConverterFunc();
        var memberConverterInstance = config.GetMemberConverterInstance();
        var memberConverterSourceExpr = config.GetMemberConverterSourceExpression();

        if ((memberConverterFunc != null || memberConverterInstance != null) && memberConverterSourceExpr != null)
        {
            propMap.MemberConverterFunc = memberConverterFunc;
            propMap.MemberConverterInstance = memberConverterInstance;
            propMap.MemberConverterSourceGetter = CompileIncludedMemberGetter(memberConverterSourceExpr);
        }
        else
        {
            var mapFromExpr = config.GetMapFromExpression();
            if (mapFromExpr != null)
                propMap.CustomMapExpression = mapFromExpr;

            var mapFromFunc = config.GetMapFromFunc();
            if (mapFromFunc != null)
                propMap.CustomMapFunc = mapFromFunc;

            var resolverType = config.GetValueResolverType();
            if (resolverType != null)
                propMap.ValueResolverType = resolverType;

            var memberResolverType = config.GetMemberValueResolverType();
            if (memberResolverType != null)
            {
                propMap.MemberValueResolverType = memberResolverType;
                var memberResolverSourceExpr = config.GetMemberValueResolverSourceExpression();
                if (memberResolverSourceExpr != null)
                    propMap.MemberValueResolverSourceGetter = CompileIncludedMemberGetter(memberResolverSourceExpr);
            }

            var mapFromName = config.GetMapFromSourceName();
            if (mapFromName != null)
            {
                var sourceProp = sourceType.GetProperty(mapFromName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (sourceProp != null)
                    propMap.SourceProperty = sourceProp;
                else
                    throw new InvalidOperationException(
                        $"Source property '{mapFromName}' not found on type '{sourceType.Name}' " +
                        $"when configuring MapFrom for '{propMap.DestinationProperty.Name}'.");
            }
        }

        var condition = config.GetConditionFunc();
        if (condition != null) propMap.Condition = condition;

        var condition3Arg = config.GetCondition3ArgFunc();
        if (condition3Arg != null) propMap.Condition3Arg = condition3Arg;

        var preCondition = config.GetPreConditionFunc();
        if (preCondition != null) propMap.PreCondition = preCondition;

        if (config.GetHasNullSubstitute())
        {
            propMap.HasNullSubstitute = true;
            propMap.NullSubstitute = config.GetNullSubstituteValue();
        }
    }

    /// <summary>
    /// Fast path for applying ForAllMembers/ForAllOtherMembers config.
    /// </summary>
    private static void ApplyForAllMembersConfigFast(PropertyMap propMap, ICompiledMemberConfig config)
    {
        if (config.GetIsIgnored())
        {
            propMap.Ignored = true;
            return;
        }

        var condition = config.GetConditionFunc();
        if (condition != null) propMap.Condition = condition;

        var condition3Arg = config.GetCondition3ArgFunc();
        if (condition3Arg != null) propMap.Condition3Arg = condition3Arg;

        var preCondition = config.GetPreConditionFunc();
        if (preCondition != null) propMap.PreCondition = preCondition;

        if (config.GetHasNullSubstitute())
        {
            propMap.HasNullSubstitute = true;
            propMap.NullSubstitute = config.GetNullSubstituteValue();
        }
    }

    private static void ApplyMemberConfig(PropertyMap propMap, object memberConfig, Type sourceType)
    {
        // Reflection fallback — used only for dynamic open generic compilations
        if (memberConfig is ICompiledMemberConfig compiled)
        {
            ApplyMemberConfigFast(propMap, compiled, sourceType);
            return;
        }

        var configType = memberConfig.GetType();

        var isIgnored = (bool)(configType.GetProperty("IsIgnored", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) ?? false);
        propMap.IsExplicitlyConfigured = true;
        if (isIgnored)
        {
            propMap.Ignored = true;
            return;
        }

        var hasConstant = (bool)(configType.GetProperty("HasConstantValue", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) ?? false);
        if (hasConstant)
        {
            propMap.HasConstantValue = true;
            propMap.ConstantValue = configType.GetProperty("ConstantValue", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig);
            return;
        }

        var memberConverterFunc = configType.GetProperty("MemberConverterFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        var memberConverterInstance = configType.GetProperty("MemberConverterInstance", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig);
        var memberConverterSourceExpr = configType.GetProperty("MemberConverterSourceExpression", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as LambdaExpression;

        if ((memberConverterFunc != null || memberConverterInstance != null) && memberConverterSourceExpr != null)
        {
            propMap.MemberConverterFunc = memberConverterFunc;
            propMap.MemberConverterInstance = memberConverterInstance;
            propMap.MemberConverterSourceGetter = CompileIncludedMemberGetter(memberConverterSourceExpr);
        }
        else
        {
            var mapFromExpr = configType.GetProperty("MapFromExpression", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig)
                as LambdaExpression;
            if (mapFromExpr != null) propMap.CustomMapExpression = mapFromExpr;

            var mapFromFunc = configType.GetProperty("MapFromFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
            if (mapFromFunc != null) propMap.CustomMapFunc = mapFromFunc;

            var resolverType = configType.GetProperty("ValueResolverType", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Type;
            if (resolverType != null) propMap.ValueResolverType = resolverType;

            var memberResolverType = configType.GetProperty("MemberValueResolverType", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Type;
            if (memberResolverType != null)
            {
                propMap.MemberValueResolverType = memberResolverType;
                var memberResolverSourceExpr = configType.GetProperty("MemberValueResolverSourceExpression", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as LambdaExpression;
                if (memberResolverSourceExpr != null)
                    propMap.MemberValueResolverSourceGetter = CompileIncludedMemberGetter(memberResolverSourceExpr);
            }

            var mapFromName = configType.GetProperty("MapFromSourceName", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as string;
            if (mapFromName != null)
            {
                var sourceProp = sourceType.GetProperty(mapFromName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (sourceProp != null)
                    propMap.SourceProperty = sourceProp;
                else
                    throw new InvalidOperationException(
                        $"Source property '{mapFromName}' not found on type '{sourceType.Name}' " +
                        $"when configuring MapFrom for '{propMap.DestinationProperty.Name}'.");
            }
        }

        var condition = configType.GetProperty("ConditionFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        if (condition != null) propMap.Condition = condition;

        var condition3Arg = configType.GetProperty("Condition3ArgFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        if (condition3Arg != null) propMap.Condition3Arg = condition3Arg;

        var preCondition = configType.GetProperty("PreConditionFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        if (preCondition != null) propMap.PreCondition = preCondition;

        var hasNullSub = (bool)(configType.GetProperty("HasNullSubstitute", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) ?? false);
        if (hasNullSub)
        {
            propMap.HasNullSubstitute = true;
            propMap.NullSubstitute = configType.GetProperty("NullSubstituteValue", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig);
        }
    }

    private static void ApplyForAllMembersConfig(PropertyMap propMap, object memberConfig)
    {
        if (memberConfig is ICompiledMemberConfig compiled)
        {
            ApplyForAllMembersConfigFast(propMap, compiled);
            return;
        }

        var configType = memberConfig.GetType();

        var isIgnored = (bool)(configType.GetProperty("IsIgnored", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) ?? false);
        if (isIgnored)
        {
            propMap.Ignored = true;
            return;
        }

        var condition = configType.GetProperty("ConditionFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        if (condition != null) propMap.Condition = condition;

        var condition3Arg = configType.GetProperty("Condition3ArgFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        if (condition3Arg != null) propMap.Condition3Arg = condition3Arg;

        var preCondition = configType.GetProperty("PreConditionFunc", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) as Delegate;
        if (preCondition != null) propMap.PreCondition = preCondition;

        var hasNullSub = (bool)(configType.GetProperty("HasNullSubstitute", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig) ?? false);
        if (hasNullSub)
        {
            propMap.HasNullSubstitute = true;
            propMap.NullSubstitute = configType.GetProperty("NullSubstituteValue", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(memberConfig);
        }
    }

    /// <summary>
    /// Wraps a typed delegate (e.g. Func&lt;TSource, TDest&gt;) into Func&lt;object, object?&gt;.
    /// Used to eliminate DynamicInvoke at mapping time.
    /// </summary>
    private static Func<object, object?> WrapToObjectFunc(Delegate del)
    {
        return src => del.DynamicInvoke(src);
    }

    /// <summary>
    /// Wraps a typed delegate into Func&lt;object, object&gt; (non-null result).
    /// </summary>
    private static Func<object, object> WrapToObjectFuncNonNull(Delegate del)
    {
        return src => del.DynamicInvoke(src)!;
    }

    /// <summary>
    /// Wraps a typed Action&lt;TSource, TDest&gt; into Action&lt;object, object&gt;.
    /// </summary>
    private static Action<object, object> WrapToObjectAction(Delegate del)
    {
        return (src, dest) => del.DynamicInvoke(src, dest);
    }

    /// <summary>
    /// Pre-compiles the Convert method invocation for a type converter instance.
    /// Eliminates GetMethod("Convert") reflection on every mapping call.
    /// </summary>
    private static Func<object, object?, ResolutionContext, object?> CompileTypeConverterInvocation(object converterInstance)
    {
        var converterType = converterInstance.GetType();
        var convertMethod = converterType.GetMethod("Convert")
            ?? converterType.GetInterfaces()
                .SelectMany(i => i.GetMethods())
                .First(m => m.Name == "Convert");

        return (source, destDefault, context) => convertMethod.Invoke(converterInstance, [source, destDefault, context]);
    }

    /// <summary>
    /// Compiles a lambda expression into a Func&lt;object, object?&gt; getter.
    /// Used for IncludeMembers and member converter source expressions.
    /// </summary>
    private static Func<object, object?> CompileIncludedMemberGetter(LambdaExpression expression)
    {
        var sourceParam = expression.Parameters[0];
        var objParam = Expression.Parameter(typeof(object), "obj");
        var castSource = Expression.Convert(objParam, sourceParam.Type);

        var body = expression.Body;
        var replaced = new ParameterReplacer(sourceParam, castSource).Visit(body);

        // Ensure boxed to object
        if (replaced.Type != typeof(object))
            replaced = Expression.Convert(replaced, typeof(object));

        return Expression.Lambda<Func<object, object?>>(replaced, objParam).Compile();
    }

    /// <summary>
    /// Compiles a field getter (for ValueTuple Item1/Item2/etc. public fields).
    /// </summary>
    private static Func<object, object?> CompileFieldGetter(FieldInfo field)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        var cast = Expression.Convert(param, field.DeclaringType!);
        var access = Expression.Field(cast, field);
        var boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
    }

    /// <summary>
    /// Attempts to flatten a destination member name into a chain of source properties.
    /// E.g., "AddressStreet" → [Address, Street] by PascalCase splitting.
    /// </summary>
    private static PropertyInfo[]? TryFlatten(string destMemberName, Type sourceType)
    {
        // Split PascalCase: "AddressStreet" → ["Address", "Street"]
        var parts = SplitPascalCase(destMemberName);
        if (parts.Count <= 1)
            return null;

        // Try progressively longer prefixes
        for (int splitAt = 1; splitAt < parts.Count; splitAt++)
        {
            var firstPart = string.Join("", parts.Take(splitAt));
            var prop = sourceType.GetProperty(firstPart, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (prop != null && prop.CanRead)
            {
                var remaining = string.Join("", parts.Skip(splitAt));
                var innerProp = prop.PropertyType.GetProperty(remaining,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (innerProp != null && innerProp.CanRead)
                {
                    return [prop, innerProp];
                }

                // Try recursive flattening
                var innerChain = TryFlatten(remaining, prop.PropertyType);
                if (innerChain != null)
                {
                    return [prop, .. innerChain];
                }
            }
        }

        return null;
    }

    private static List<string> SplitPascalCase(string name)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            current.Append(name[i]);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParam;
        private readonly Expression _newExpr;

        public ParameterReplacer(ParameterExpression oldParam, Expression newExpr)
        {
            _oldParam = oldParam;
            _newExpr = newExpr;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _oldParam ? _newExpr : base.VisitParameter(node);
        }
    }
}
