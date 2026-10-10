namespace AIF.Store;

public sealed class AmdRepositoryScanner
{
    private readonly string repositoryPath;

    public AmdRepositoryScanner(string repositoryPath)
    {
        this.repositoryPath = repositoryPath;
    }

    public IReadOnlyList<string> Scan()
    {
        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"AMD repository not found: {repositoryPath}");
        }

        return Directory
            .GetFiles(repositoryPath, "*.json")
            .OrderBy(f => f)
            .ToList();
    }
}
