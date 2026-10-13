// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHAT A COPY MADE THAT IS NOT A COPY OF A SOURCE.
//
// Copying a wall copies the door in it: Revit returns both ids. The copy and array
// checks demanded exactly one created element per source, so a correct copy of a
// wall with an insert failed its verification after the commit (audit 2026-10-08,
// 6.8). An extra element is now accepted as a DEPENDENT when it is hosted, directly
// or through another dependent, on an element this operation created; anything
// else Revit made is unexplained and still fails the check.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class TransformCopyRules
    {
        /// <summary>
        /// Split the created elements that are not matched copies into dependents (hosted,
        /// transitively, on a copy) and unexplained ones.
        /// </summary>
        public static void Classify(IEnumerable<long> extras, IEnumerable<long> copies, Func<long, long?> hostOf,
                                    out List<long> dependents, out List<long> unexplained)
        {
            var anchored = new HashSet<long>(copies ?? Enumerable.Empty<long>());
            var pending = new List<long>((extras ?? Enumerable.Empty<long>()).Distinct().Where(id => !anchored.Contains(id)));
            dependents = new List<long>();
            bool grew = true;
            while (grew && pending.Count > 0)
            {
                grew = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    long? host = hostOf(pending[i]);
                    if (host.HasValue && anchored.Contains(host.Value))
                    {
                        anchored.Add(pending[i]);
                        dependents.Add(pending[i]);
                        pending.RemoveAt(i);
                        grew = true;
                    }
                }
            }
            dependents.Sort();
            unexplained = pending.OrderBy(x => x).ToList();
        }
    }
}
