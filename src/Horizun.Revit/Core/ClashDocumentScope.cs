using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{

    public static class ClashBooleanRetry
    {
        /// <summary>Retry the same exact intersection with reversed operands. No movement,
        /// approximation or tolerance change; both failures remain unknown.</summary>
        public static bool Try<T>(Func<T> first, Func<T> reversed, out T result, out bool recovered, out string error)
        {
            recovered = false;
            error = null;
            try { result = first(); return true; }
            catch (Exception ex) { error = ex.Message; }
            try { result = reversed(); recovered = true; error = null; return true; }
            catch (Exception ex) { error += " | reversed operands: " + ex.Message; result = default(T); return false; }
        }
    }
}
