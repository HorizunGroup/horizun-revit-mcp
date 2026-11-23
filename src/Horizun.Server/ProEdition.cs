using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ProEdition
    {
        internal static void StampHealth(JObject health)
        {
            health["edition"] = Horizun.Contracts.Edition.Name;
        }
    }
}
