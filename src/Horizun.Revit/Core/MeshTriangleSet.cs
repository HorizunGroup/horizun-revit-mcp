using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    // One set per element per host/link path.
    public sealed class MeshTriangleSet
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        public bool Add(double[][] vertices) => _seen.Add(Key(vertices));
        internal bool Contains(double[][] vertices) => _seen.Contains(Key(vertices));

        private static string Key(double[][] vertices) => string.Join(";", vertices
            .Select(p => string.Join(",", p.Select(v => v == 0 ? "0" : v.ToString("R", CultureInfo.InvariantCulture))))
            .OrderBy(p => p, StringComparer.Ordinal));
    }

    internal sealed class MeshHierarchyFaces
    {
        private readonly Dictionary<long, List<MeshSurface>> _parentsByChild = new Dictionary<long, List<MeshSurface>>();

        // Drain each element's faces before moving to the next: a child's coverage
        // is the geometry already written for its parents on this document/link path.
        internal void Collect<T>(IEnumerable<T> direct, Func<T, long> id, Func<T, bool> container,
            Func<T, IEnumerable<T>> children, bool includeChildren, Func<T, IEnumerable<double[][]>> read,
            Action<T, IEnumerable<double[][]>> emit)
        {
            var visited = new HashSet<long>();
            var leaves = new Queue<T>();
            var implicitChildren = new Queue<T>();
            void Visit(T item)
            {
                long itemId = id(item);
                if (!visited.Add(itemId)) return;
                MeshSurface surface = null;
                if (container(item))
                    foreach (T child in children(item))
                    {
                        long childId = id(child);
                        if (!_parentsByChild.TryGetValue(childId, out var parents))
                            _parentsByChild.Add(childId, parents = new List<MeshSurface>());
                        parents.Add(surface ?? (surface = new MeshSurface()));
                        if (includeChildren) implicitChildren.Enqueue(child);
                    }
                _parentsByChild.TryGetValue(itemId, out var coverage);
                emit(item, VisibleFaces(read(item), coverage, surface));
            }

            foreach (T item in direct)
            {
                if (container(item)) Visit(item);
                else leaves.Enqueue(item);
            }
            while (leaves.Count > 0) Visit(leaves.Dequeue());
            while (implicitChildren.Count > 0) Visit(implicitChildren.Dequeue());
        }

        private static IEnumerable<double[][]> VisibleFaces(IEnumerable<double[][]> faces, List<MeshSurface> coverage, MeshSurface retain)
        {
            var local = new MeshTriangleSet();
            foreach (double[][] face in faces)
            {
                if (!local.Add(face)) continue;
                foreach (double[][] visible in MeshSurface.Uncovered(face, coverage))
                {
                    retain?.Add(visible);
                    yield return visible;
                }
            }
        }
    }
}
