using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    internal enum CategoryResolution { Found, RawBuiltIn, Unknown }

    // How a requested category name filters a query (horizun_query_model categories).
    internal static class CategoryNameRules
    {
        // Found: the model gave a Category object. RawBuiltIn: a defined BuiltInCategory with no Category object
        // (OST_Viewers on Revit 2025; its section marks are still in plan views) - it still filters by its id.
        // Unknown: neither - nothing of it can be counted.
        internal static CategoryResolution Classify(bool categoryFound, bool definedBuiltIn) =>
            categoryFound ? CategoryResolution.Found
            : definedBuiltIn ? CategoryResolution.RawBuiltIn
            : CategoryResolution.Unknown;
    }

    // One decision after every source the query read (host, links, or links only): a name no source could resolve
    // is reported once - a typo must not come back as a confident zero - and a name one source lacks (an import
    // category of one link) is not. A source whose category table could not be read counts as not judging the name:
    // that failure is reported on its own.
    internal sealed class CategoryNameTally
    {
        private readonly Dictionary<string, bool> _resolved = new Dictionary<string, bool>(StringComparer.Ordinal);

        internal void Read(string name, CategoryResolution resolution, bool tableReadFailed)
        {
            bool judgedResolved = resolution != CategoryResolution.Unknown || tableReadFailed;
            _resolved[name] = (_resolved.TryGetValue(name, out bool before) && before) || judgedResolved;
        }

        internal IEnumerable<string> UnresolvedEverywhere() => _resolved.Where(kv => !kv.Value).Select(kv => kv.Key);
    }
}
