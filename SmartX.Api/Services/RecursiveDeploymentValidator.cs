using SmartX.Api.Models;

namespace SmartX.Api.Services;

/// <summary>
/// RECURSION REQUIREMENT (algorithm side).
///
/// Validates that a sensor's dotted DeploymentPath (e.g. "Facility A/Zone 1/Sub-Zone B")
/// is a legal, already-configured path through the facility's deployment tree, by
/// recursively descending one path segment per call. This mirrors how the physical
/// mesh is actually structured (a facility contains zones, a zone contains
/// sub-zones, a sub-zone contains nodes) and lets us validate arbitrarily deep
/// nesting without hard-coding a fixed number of tiers.
/// </summary>
public class RecursiveDeploymentValidator
{
    private readonly DeploymentNode _root;

    public RecursiveDeploymentValidator(DeploymentNode root)
    {
        _root = root;
    }

    /// <summary>
    /// Entry point: splits "Facility A/Zone 1/Sub-Zone B" into segments and
    /// recursively validates them against the configured tree.
    /// </summary>
    public bool IsValidPath(string deploymentPath, out string message)
    {
        if (string.IsNullOrWhiteSpace(deploymentPath))
        {
            message = "Deployment path cannot be empty.";
            return false;
        }

        var segments = deploymentPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            message = "Deployment path did not contain any valid segments.";
            return false;
        }

        // The first segment must match the tree's root (the facility).
        if (!string.Equals(segments[0], _root.Name, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Unknown facility '{segments[0]}'. Expected root '{_root.Name}'.";
            return false;
        }

        return ValidateRecursive(_root, segments, depth: 1, out message);
    }

    /// <summary>
    /// The recursive step: given we are currently positioned at <paramref name="currentNode"/>
    /// (already matched at index depth-1), confirm the next path segment exists as a
    /// child and recurse into it. The base case is reached once every segment has
    /// been consumed.
    /// </summary>
    private bool ValidateRecursive(DeploymentNode currentNode, string[] segments, int depth, out string message)
    {
        // BASE CASE: every segment has been matched down the tree - success.
        if (depth == segments.Length)
        {
            message = $"Path valid: node safely configured within {string.Join(" -> ", segments.Reverse())}.";
            return true;
        }

        var nextSegment = segments[depth];
        var child = currentNode.FindChild(nextSegment);

        if (child is null)
        {
            message = $"'{nextSegment}' is not a configured child of '{currentNode.Name}'. " +
                      $"Available: [{string.Join(", ", currentNode.Children.Select(c => c.Name))}]";
            return false;
        }

        // RECURSIVE CASE: descend one tier and continue.
        return ValidateRecursive(child, segments, depth + 1, out message);
    }

    /// <summary>
    /// Convenience factory building the default demo facility tree used to seed
    /// the simulation: Facility A -> Zone 1 -> Sub-Zone A / Sub-Zone B,
    /// Facility A -> Zone 2 -> Sub-Zone C.
    /// </summary>
    public static DeploymentNode BuildDefaultFacilityTree() => new()
    {
        Name = "Facility A",
        Children =
        {
            new DeploymentNode
            {
                Name = "Zone 1",
                Children =
                {
                    new DeploymentNode { Name = "Sub-Zone A" },
                    new DeploymentNode { Name = "Sub-Zone B" }
                }
            },
            new DeploymentNode
            {
                Name = "Zone 2",
                Children =
                {
                    new DeploymentNode { Name = "Sub-Zone C" }
                }
            }
        }
    };
}
