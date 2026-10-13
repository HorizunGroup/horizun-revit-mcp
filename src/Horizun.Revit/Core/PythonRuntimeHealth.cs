using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class PythonRuntimeHealth
    {
        internal static JObject Snapshot(bool authorized)
        {
            return Snapshot(authorized, RuntimeWarmup.Python);
        }

        internal static JObject Snapshot(bool authorized, RuntimeWarmup runtime)
        {
            JObject warmup = runtime.Snapshot();
            string lifecycle = (string)warmup["state"];
            warmup["state"] = !authorized ? "not_authorized" :
                lifecycle == "failed" ? "not_loadable" :
                lifecycle == "ready" ? "warm" : "cold";
            // A warm engine can serve shipped recipes even if arbitrary Python
            // remains disallowed. Expose that fact without implying permission.
            warmup["engine_state"] = lifecycle;
            warmup["authorized"] = authorized;
            return warmup;
        }
    }
}
