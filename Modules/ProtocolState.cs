using VRPC.Application;

namespace VRPC
{
    // Wire-protocol version negotiation between extension and app.
    public static class ProtocolState
    {
        public const int ServerProtocol = 2;
        public static int ClientProtocol { get; private set; } = 1;

        public static void SetClientProtocol(string? payload)
        {
            if (!int.TryParse(payload?.Trim(), out int protocol)) { return; }
            if (protocol < 1) { return; }
            ClientProtocol = Math.Min(protocol, ServerProtocol);
        }

        public static string AppVersionPayload()
        {
            return ClientProtocol >= 2
                ? $"{ProductInfo.Version};PROTO={ServerProtocol}"
                : ProductInfo.Version;
        }
    }
}
