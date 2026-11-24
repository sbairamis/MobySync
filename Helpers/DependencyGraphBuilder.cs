using Docker.DotNet.Models;

namespace MobySync.Helpers;

public class DependencyGraphBuilder
{
    public Dictionary<string, List<ContainerListResponse>> GroupDependentContainers(IList<ContainerListResponse> monitoredContainers)
    {
        var containerMap = monitoredContainers.ToDictionary(
            container => container.Names.First().Replace("/", ""),
            container => container);

        var adjacencyList = new Dictionary<string, HashSet<string>>();

        foreach (var name in containerMap.Keys)
        {
            adjacencyList[name] = new HashSet<string>();
        }

        foreach (var container in monitoredContainers)
        {
            var containerName = container.Names.First().Replace("/", "");

            if (container.Labels != null && container.Labels.TryGetValue("com.mobysync.depends-on", out var deps))
            {
                foreach (var dependency in deps.Split(',').Select(dependency => dependency.Trim()))
                {
                    if (containerMap.ContainsKey(dependency))
                    {
                        adjacencyList[containerName].Add(dependency);
                        adjacencyList[dependency].Add(containerName);
                    }
                }
            }
        }

        var visited = new HashSet<string>();
        var groupedDictionary = new Dictionary<string, List<ContainerListResponse>>();
        var groupCounter = 1;

        foreach (var nodeName in containerMap.Keys)
        {
            if (!visited.Contains(nodeName))
            {
                var currentGroupNames = new List<string>();
                ExploreNode(nodeName, currentGroupNames);

                var containersGroup = currentGroupNames.Select(name => containerMap[name]).ToList();
                var sortedGroup = SortContainersByDependencies(containersGroup);

                groupedDictionary.Add($"Auto-Group-{groupCounter}", sortedGroup);
                groupCounter++;
            }
        }

        return groupedDictionary;

        void ExploreNode(string current, List<string> group)
        {
            visited.Add(current);
            group.Add(current);

            foreach (var neighbor in adjacencyList[current])
            {
                if (!visited.Contains(neighbor))
                {
                    ExploreNode(neighbor, group);
                }
            }
        }
    }

    public List<ContainerListResponse> SortContainersByDependencies(IList<ContainerListResponse> containers)
    {
        // This list is sorted so that base dependencies update first, and dependent containers update lastly
        var sortedList = new List<ContainerListResponse>();

        // Set used for containers that have been processed
        var visited = new HashSet<string>();

        // Set used to detect circular dependencies
        var visiting = new HashSet<string>();

        var containerMap = containers.ToDictionary(
            container => container.Names.First().Replace("/", ""),
            container => container);

        foreach (var container in containers)
        {
            Visit(container);
        }

        return sortedList;

        void Visit(ContainerListResponse container)
        {
            var containerName = container.Names.First().Replace("/", "");

            if (visited.Contains(containerName))
                return;

            if (visiting.Contains(containerName))
                throw new InvalidOperationException($"An circular dependency has detected involving containers '{containerName}'");

            visiting.Add(containerName);

            if (container.Labels != null && container.Labels.TryGetValue("com.mobysync.depends-on", out var dependenciesString))
            {
                var dependencies = dependenciesString.Split(',')
                    .Select(dependency => dependency.Trim());

                foreach (var dependency in dependencies)
                {
                    if (containerMap.TryGetValue(dependency, out var dependencyContainer))
                    {
                        Visit(dependencyContainer);
                    }
                }
            }

            visiting.Remove(containerName);
            visited.Add(containerName);
            sortedList.Add(container);
        }
    }
}
