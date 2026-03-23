namespace Meridian.Mediator;

internal sealed class OpenRequestHandlerRegistry
{
    private readonly List<OpenRequestHandlerEntry> _entries = new();

    public bool HasEntries => _entries.Count > 0;

    public void Add(Type handlerType, Type serviceInterfaceType)
    {
        _entries.Add(new OpenRequestHandlerEntry(handlerType, serviceInterfaceType));
    }

    public bool TryResolveHandlerType(Type requestType, Type responseType, out Type closedHandlerType)
    {
        foreach (var entry in _entries)
        {
            if (entry.TryResolve(requestType, responseType, out closedHandlerType))
            {
                return true;
            }
        }

        closedHandlerType = null!;
        return false;
    }

    private sealed class OpenRequestHandlerEntry
    {
        private readonly Type _handlerType;
        private readonly Type _requestPattern;
        private readonly Type _responsePattern;

        public OpenRequestHandlerEntry(Type handlerType, Type serviceInterfaceType)
        {
            _handlerType = handlerType;

            if (!serviceInterfaceType.IsGenericType)
            {
                throw new ArgumentException("Service interface type must be generic.", nameof(serviceInterfaceType));
            }

            var args = serviceInterfaceType.GetGenericArguments();
            _requestPattern = args[0];
            _responsePattern = args[1];
        }

        public bool TryResolve(Type requestType, Type responseType, out Type closedHandlerType)
        {
            var genericArgValues = new Dictionary<Type, Type>();
            if (!TryMatchPattern(_requestPattern, requestType, genericArgValues))
            {
                closedHandlerType = null!;
                return false;
            }

            if (!TryMatchPattern(_responsePattern, responseType, genericArgValues))
            {
                closedHandlerType = null!;
                return false;
            }

            var genericArgs = _handlerType.GetGenericArguments();
            var concreteArgs = new Type[genericArgs.Length];

            for (var i = 0; i < genericArgs.Length; i++)
            {
                if (!genericArgValues.TryGetValue(genericArgs[i], out var concreteArg))
                {
                    closedHandlerType = null!;
                    return false;
                }

                concreteArgs[i] = concreteArg;
            }

            closedHandlerType = _handlerType.MakeGenericType(concreteArgs);
            return !closedHandlerType.ContainsGenericParameters;
        }

        private static bool TryMatchPattern(Type pattern, Type concrete, Dictionary<Type, Type> genericArgValues)
        {
            if (pattern.IsGenericParameter)
            {
                if (!genericArgValues.TryGetValue(pattern, out var existing))
                {
                    genericArgValues[pattern] = concrete;
                    return true;
                }

                return existing == concrete;
            }

            if (pattern.IsArray)
            {
                return concrete.IsArray && concrete.GetElementType() == pattern.GetElementType();
            }

            if (!pattern.IsGenericType)
            {
                return pattern == concrete;
            }

            if (!concrete.IsGenericType)
            {
                return false;
            }

            if (pattern.GetGenericTypeDefinition() != concrete.GetGenericTypeDefinition())
            {
                return false;
            }

            var patternArgs = pattern.GetGenericArguments();
            var concreteArgs = concrete.GetGenericArguments();
            for (var i = 0; i < patternArgs.Length; i++)
            {
                if (!TryMatchPattern(patternArgs[i], concreteArgs[i], genericArgValues))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
