using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    internal sealed class CopySampleMatching
    {
        private readonly SpatialIndex root;
        private long nodeVisits;

        private static readonly IComparer<Node>[] AxisOrder = Enumerable.Range(0, 3)
            .Select(axis => Comparer<Node>.Create((a, b) =>
            {
                int order = a.Min[axis].CompareTo(b.Min[axis]);
                return order != 0 ? order : a.Index.CompareTo(b.Index);
            })).ToArray();

        private CopySampleMatching(double[][] points) =>
            root = BuildIndex(points, Enumerable.Range(0, points.Length).ToArray());

        // A balanced spatial index of the destination samples. Counts let a
        // query skip subtrees whose points have already been consumed.
        private static SpatialIndex BuildIndex(double[][] points, int[] indices)
        {
            var comparers = Enumerable.Range(0, 3).Select(axis => Comparer<int>.Create((a, b) =>
            {
                int order = points[a][axis].CompareTo(points[b][axis]);
                return order != 0 ? order : a.CompareTo(b);
            })).ToArray();
            return new SpatialIndex(Build(0, indices.Length, 0, null));

            Node Build(int start, int count, int splitAxis, Node parent)
            {
                var node = new Node(parent, count);
                double[] first = points[indices[start]];
                if (count == 1)
                {
                    node.Min = node.Max = first;
                    node.Index = indices[start];
                    return node;
                }

                node.Min = (double[])first.Clone();
                node.Max = (double[])first.Clone();
                for (int i = start + 1; i < start + count; i++)
                {
                    double[] point = points[indices[i]];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        node.Min[axis] = Math.Min(node.Min[axis], point[axis]);
                        node.Max[axis] = Math.Max(node.Max[axis], point[axis]);
                    }
                }
                // Rotate median splits even when a different axis has a larger
                // span. Otherwise every subtree can straddle a narrow query
                // boundary in two axes while none of its points lies inside.
                for (int skipped = 0; skipped < 2 && node.Min[splitAxis] == node.Max[splitAxis]; skipped++)
                    splitAxis = (splitAxis + 1) % 3;
                Array.Sort(indices, start, count, comparers[splitAxis]);
                int half = count / 2;
                int nextAxis = (splitAxis + 1) % 3;
                node.Left = Build(start, half, nextAxis, node);
                node.Right = Build(start + half, count - half, nextAxis, node);
                return node;
            }
        }

        public static bool HasCompleteMatching(double[][] source, double[][] destination, double tolerance,
            out long nodeVisits)
        {
            var index = new CopySampleMatching(destination);
            bool matched = index.Match(source, destination, tolerance);
            nodeVisits = index.nodeVisits;
            return matched;
        }

        private bool Match(double[][] source, double[][] destination, double tolerance)
        {
            var paired = new bool[source.Length];
            int[] owner = Enumerable.Repeat(-1, source.Length).ToArray();
            var unmatched = new List<int>();
            var taken = new List<Node>();
            for (int i = 0; i < source.Length; i++)
            {
                Node found = Take(root, source[i], tolerance);
                if (found == null)
                {
                    // A source with no geometric candidate cannot be rescued by rearranging pairs.
                    // Once the exact range index is built, prove that before searching more sources.
                    if (root.HasNoCandidate(source[i], tolerance, ref nodeVisits)) return false;
                    unmatched.Add(i);
                }
                else
                {
                    paired[i] = true;
                    owner[found.Index] = i;
                    taken.Add(found);
                }
            }
            if (unmatched.Count == 0) return true;
            foreach (Node node in taken)
                root.ChangeAvailability(node, 1);

            // Augment along disjoint shortest paths in one pass through each
            // set of layers. A failed branch cannot become available later in
            // this pass, so all paths share the same depleted layer indices.
            var path = new List<int>();
            while (unmatched.Count > 0)
            {
                List<SpatialIndex> layers = BuildLayers(source, destination, unmatched, owner, tolerance, taken);
                if (layers == null) return false;
                foreach (int start in unmatched)
                    paired[start] = Augment(start, source, owner, layers, path, tolerance);
                unmatched.RemoveAll(i => paired[i]);
            }
            return true;
        }

        private List<SpatialIndex> BuildLayers(double[][] source, double[][] destination, List<int> unmatched,
            int[] owner, double tolerance, List<Node> taken)
        {
            taken.Clear();
            var layers = new List<int[]>();
            var frontier = unmatched;
            while (frontier.Count > 0)
            {
                var next = new List<int>();
                var occupied = new List<int>();
                var free = new List<int>();
                foreach (int current in frontier)
                {
                    Node found;
                    while ((found = Take(root, source[current], tolerance)) != null)
                    {
                        taken.Add(found);
                        int previousOwner = owner[found.Index];
                        if (previousOwner < 0) free.Add(found.Index);
                        else
                        {
                            occupied.Add(found.Index);
                            next.Add(previousOwner);
                        }
                    }
                }
                if (free.Count > 0)
                {
                    layers.Add(free.ToArray());
                    foreach (Node node in taken)
                        root.ChangeAvailability(node, 1);
                    return layers.Select(indices => BuildIndex(destination, indices)).ToList();
                }
                if (occupied.Count > 0) layers.Add(occupied.ToArray());
                frontier = next;
            }
            return null;
        }

        private bool Augment(int start, double[][] source, int[] owner, List<SpatialIndex> layers, List<int> path,
            double tolerance)
        {
            path.Clear();
            int current = start;
            while (path.Count < layers.Count)
            {
                Node found = Take(layers[path.Count], source[current], tolerance);
                if (found == null)
                {
                    if (path.Count == 0) return false;
                    path.RemoveAt(path.Count - 1);
                    current = path.Count == 0 ? start : owner[path[path.Count - 1]];
                }
                else
                {
                    path.Add(found.Index);
                    current = owner[found.Index];
                }
            }
            current = start;
            foreach (int candidate in path)
            {
                int previousOwner = owner[candidate];
                owner[candidate] = current;
                current = previousOwner;
            }
            return true;
        }

        private Node Take(SpatialIndex index, double[] point, double tolerance)
        {
            Node found = index.Find(point, tolerance, ref nodeVisits);
            if (found != null) index.ChangeAvailability(found, -1);
            return found;
        }

        private sealed class SpatialIndex
        {
            private const int SpatialVisitLimit = 64;
            private readonly Node rootNode;
            private RangeNode ranges;

            public SpatialIndex(Node rootNode) => this.rootNode = rootNode;

            public Node Find(double[] point, double tolerance, ref long visits)
            {
                if (ranges == null)
                {
                    int remaining = SpatialVisitLimit;
                    Node found = FindSpatial(rootNode, point, tolerance, ref remaining);
                    visits += SpatialVisitLimit - remaining;
                    if (found != null || remaining != 0) return found;

                    // Bounding boxes can straddle multiple limits all the way down a spatial tree.
                    // Keep that search bounded and switch this index to exact axis ranges on demand.
                    var leaves = new List<Node>();
                    CollectLeaves(rootNode, leaves);
                    ranges = RangeNode.Build(leaves.ToArray(), 0, rootNode.Available);
                }
                return ranges.Find(point, tolerance, ref visits);
            }

            public bool HasNoCandidate(double[] point, double tolerance, ref long visits) =>
                ranges != null && ranges.Find(point, tolerance, ref visits, includeTaken: true) == null;

            private static Node FindSpatial(Node node, double[] point, double tolerance, ref int remaining)
            {
                if (remaining == 0) return null;
                remaining--;
                if (node.Available == 0) return null;
                for (int axis = 0; axis < 3; axis++)
                    if (node.Min[axis] - point[axis] > tolerance || point[axis] - node.Max[axis] > tolerance)
                        return null;
                if (node.Left == null) return node;
                return FindSpatial(node.Left, point, tolerance, ref remaining) ??
                    FindSpatial(node.Right, point, tolerance, ref remaining);
            }

            private static void CollectLeaves(Node node, List<Node> leaves)
            {
                if (node.Left == null) leaves.Add(node);
                else
                {
                    CollectLeaves(node.Left, leaves);
                    CollectLeaves(node.Right, leaves);
                }
            }

            public void ChangeAvailability(Node sample, int delta)
            {
                sample.ChangeAvailability(delta);
                ranges?.ChangeAvailability(sample, delta);
            }
        }

        // Decompose a query into disjoint in-range segments on one axis, then query
        // each segment on the next axis. Three axes give O(log^3 n) queries and
        // updates. Build associated ranges only when queried; points consumed
        // before a range is built already carry their current availability.
        private sealed class RangeNode
        {
            private const int LeafSize = 8;
            private readonly Node[] samples;
            private readonly int start, count, axis;
            private RangeNode left, right, nextAxis;
            private int available;

            private RangeNode(Node[] sorted, int start, int count, int axis, int available)
            {
                samples = sorted;
                this.start = start;
                this.count = count;
                this.axis = axis;
                this.available = available;
            }

            public static RangeNode Build(Node[] nodes, int axis, int available)
            {
                Array.Sort(nodes, AxisOrder[axis]);
                return new RangeNode(nodes, 0, nodes.Length, axis, available);
            }

            public Node Find(double[] point, double tolerance, ref long visits, bool includeTaken = false)
            {
                visits++;
                if (!includeTaken && available == 0) return null;
                if (count <= LeafSize)
                {
                    for (int i = start; i < start + count; i++)
                    {
                        visits++;
                        Node sample = samples[i];
                        if ((includeTaken || sample.Available > 0) && CopyRepresentationRules.Delta(sample.Min, point) <= tolerance)
                            return sample;
                    }
                    return null;
                }

                double coordinate = point[axis];
                double min = samples[start].Min[axis], max = samples[start + count - 1].Min[axis];
                if (min - coordinate > tolerance || coordinate - max > tolerance) return null;
                if (axis < 2 && coordinate - min <= tolerance && max - coordinate <= tolerance)
                {
                    if (nextAxis == null)
                    {
                        var byNextAxis = new Node[count];
                        Array.Copy(samples, start, byNextAxis, 0, count);
                        nextAxis = Build(byNextAxis, axis + 1, available);
                    }
                    return nextAxis.Find(point, tolerance, ref visits, includeTaken);
                }
                if (left == null) Split();
                return left.Find(point, tolerance, ref visits, includeTaken) ??
                    right.Find(point, tolerance, ref visits, includeTaken);
            }

            private void Split()
            {
                int half = count / 2;
                int leftAvailable = 0;
                for (int i = start; i < start + half; i++) leftAvailable += samples[i].Available;
                left = new RangeNode(samples, start, half, axis, leftAvailable);
                right = new RangeNode(samples, start + half, count - half, axis, available - leftAvailable);
            }

            public void ChangeAvailability(Node sample, int delta)
            {
                available += delta;
                nextAxis?.ChangeAvailability(sample, delta);
                if (left != null)
                {
                    Node pivot = samples[right.start];
                    RangeNode child = AxisOrder[axis].Compare(sample, pivot) < 0 ? left : right;
                    child.ChangeAvailability(sample, delta);
                }
            }
        }

        private sealed class Node
        {
            private readonly Node parent;
            public int Available, Index;
            public double[] Min, Max;
            public Node Left, Right;

            public Node(Node parent, int count)
            {
                this.parent = parent;
                Available = count;
            }

            public void ChangeAvailability(int delta)
            {
                for (Node node = this; node != null; node = node.parent)
                    node.Available += delta;
            }
        }
    }
}
