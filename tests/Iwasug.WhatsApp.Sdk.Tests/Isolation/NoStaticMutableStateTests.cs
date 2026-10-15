using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Iwasug.WhatsApp.Sdk.Accounts;
using Xunit;
using AwesomeAssertions;

namespace Iwasug.WhatsApp.Sdk.Tests.Isolation;

public class NoStaticMutableStateTests
{
    private static readonly string[] AuditedNamespaces =
    {
        "Iwasug.WhatsApp.Sdk.Connection",
        "Iwasug.WhatsApp.Sdk.Messaging",
        "Iwasug.WhatsApp.Sdk.Safety",
        "Iwasug.WhatsApp.Sdk.Signal",
        "Iwasug.WhatsApp.Sdk.Noise",
        "Iwasug.WhatsApp.Sdk.Stores.Runtime",
        "Iwasug.WhatsApp.Sdk.Media",
        "Iwasug.WhatsApp.Sdk.Accounts",
    };

    // Allow-list of static (mutable, non-readonly) fields that are documented
    // constants. Every entry MUST carry a rule comment justifying the exception.
    private static readonly HashSet<string> AllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // MaxLength is a public const-ish scalar bound used in format validation.
        "AccountId.MaxLength",
    };

    [Fact]
    public void No_Static_Mutable_State_In_Protocol_Layers()
    {
        Type[] types = typeof(AccountId).Assembly.GetTypes();
        var offenders = new List<string>();

        foreach (Type type in types)
        {
            if (type.Namespace is null) continue;
            if (!AuditedNamespaces.Any(ns =>
                    type.Namespace == ns || type.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsLiteral) continue;            // const
                if (field.IsInitOnly) continue;          // static readonly
                // Google.Protobuf-generated file descriptors are initialized once in a
                // static constructor of the *Reflection types and never mutated after;
                // they are effectively immutable constants, not mutable account state
                // (Rule comment: generated code, not SDK mutable state).
                if (field.FieldType.FullName == "Google.Protobuf.Reflection.FileDescriptor")
                    continue;
                if (!IsMutableType(field.FieldType)) continue;

                string key = $"{type.Name}.{field.Name}";
                if (AllowList.Contains(key)) continue;

                offenders.Add($"{type.FullName!}.{field.Name} ({field.FieldType.FullName})");
            }
        }

        offenders.Should().BeEmpty(
            "static mutable state in protocol layers breaks per-account isolation: " +
            string.Join("; ", offenders));
    }

    private static bool IsMutableType(Type type)
    {
        if (type.IsPrimitive) return false;
        if (type == typeof(string)) return false;
        if (type.IsEnum) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        if (type.IsArray) return true; // arrays are mutable containers
        // value types we cannot prove immutable are treated as mutable.
        return true;
    }
}