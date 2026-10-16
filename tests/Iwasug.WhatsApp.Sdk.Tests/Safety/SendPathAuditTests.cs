using System.Reflection;
using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Connection;
using Iwasug.WhatsApp.Sdk.Messaging;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

public enum ProtocolEgress
{
    Ack,
    Receipt,
    RetryResponder,
}

public sealed class SendPathAuditTests
{
    [Fact]
    public void every_send_node_egress_site_is_whitelisted_or_gated()
    {
        MethodInfo? sendNode = typeof(WaConnection).GetMethod("SendNodeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        sendNode.Should().NotBeNull();
        byte[] tokenBytes = BitConverter.GetBytes(sendNode!.MetadataToken);

        var assembly = typeof(WaConnection).Assembly;
        var found = new List<(string TypeName, string MethodName)>();

        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                MethodBody? body = null;
                try { body = method.GetMethodBody(); }
                catch { body = null; }
                if (body is null) continue;
                byte[] il = body.GetILAsByteArray() ?? [];
                if (Contains(il, tokenBytes))
                {
                    string ownerType = type.DeclaringType?.Name ?? type.Name;
                    string ownerMethod = method.Name;
                    if (method.Name == "MoveNext" && type.Name.StartsWith("<") && type.Name.Contains(">"))
                    {
                        ownerMethod = type.Name.TrimStart('<').Split('>')[0];
                    }
                    found.Add((ownerType, ownerMethod));
                }
            }
        }

        var allowed = new HashSet<(string, string)>
        {
            ("MessageDispatcher", "SendAckAsync"),
            ("ReceiptHandler", "SendDeliveryReceiptAsync"),
            ("ReceiptHandler", "SendReadReceiptsAsync"),
            ("ReceiptHandler", "EmitRetryReceiptAsync"),
            ("ReceiptHandler", "SendServerErrorReceiptAsync"),
            ("ReceiptHandler", "EmitServerErrorReceiptAsync"),
            ("ReceiptHandler", "SendMediaRetryReceiptAsync"),
            ("MessageSender", "AwaitAckAsync"),
            ("MessageSender", "ResendForRetryReceiptAsync"),
            ("PresenceManager", "SendAsync"),
        };

        var unexpected = found
            .Distinct()
            .Where(p => !allowed.Contains(p))
            .ToList();

        unexpected.Should().BeEmpty(
            "unexpected WaConnection.SendNodeAsync egress site(s): " +
            string.Join(", ", unexpected.Select(p => $"{p.TypeName}.{p.MethodName}")));
    }

    [Fact]
    public void every_public_media_send_method_is_safety_gated()
    {
        Type? gateType = typeof(WaConnection).Assembly.GetType("Iwasug.WhatsApp.Sdk.Safety.SafetyGate");
        gateType.Should().NotBeNull();
        MethodInfo? gateMethod = gateType!.GetMethod("ExecuteSendAsync", BindingFlags.Public | BindingFlags.Instance /* concrete internal method */);
        gateMethod.Should().NotBeNull();
        byte[] tokenBytes = BitConverter.GetBytes(gateMethod!.MetadataToken);

        var assembly = typeof(WaConnection).Assembly;
        var found = new HashSet<string>();

        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                MethodBody? body = null;
                try { body = method.GetMethodBody(); }
                catch { body = null; }
                if (body is null) continue;
                byte[] il = body.GetILAsByteArray() ?? [];
                if (!Contains(il, tokenBytes)) continue;

                string ownerMethod = method.Name;
                if (method.Name == "MoveNext" && type.Name.StartsWith("<") && type.Name.Contains(">"))
                {
                    ownerMethod = type.Name.TrimStart('<').Split('>')[0];
                }

                found.Add(ownerMethod);
            }
        }

        string[] mediaSends =
        {
            "SendImageAsync",
            "SendDocumentAsync",
            "SendVoiceNoteAsync",
            "SendGroupImageAsync",
            "SendGroupDocumentAsync",
            "SendGroupVoiceNoteAsync",
        };

        foreach (string methodName in mediaSends)
        {
            found.Should().Contain(methodName, $"{methodName} must IL-reference ISafetyGate.ExecuteSendAsync");
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
