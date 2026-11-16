using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Meridian.Mediator.Generators.MeridianMediatorGenerator;

namespace Meridian.Mediator.Generators.Emission;

internal static class NotificationRegistryEmitter
{
    internal static void Emit(StringBuilder builder, IEnumerable<Entry> entries)
    {
        builder.Append("public global::Meridian.Mediator.Wrappers.NotificationHandlerWrapperBase? FindNotificationHandler(global::System.Type notificationType) {\n");
        foreach (var entry in entries.Where(e => e.Category == "NotificationHandler").GroupBy(e => TypeName(e.Contract.TypeArguments[0])).Select(g => g.First()))
        {
            var name = TypeName(entry.Contract.TypeArguments[0]);
            builder.Append("if (notificationType == typeof(").Append(name).Append(")) return new global::Meridian.Mediator.Wrappers.NotificationHandlerWrapperImpl<").Append(name).Append(">();\n");
        }
        builder.Append("return null; }\n");
    }
}
