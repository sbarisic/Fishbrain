using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fishbrain.DataGenerator;

internal sealed record SourceManifest(SourceDefinition[] Sources);
internal sealed record SourceDefinition(
	string Name,
	string Revision,
	string License,
	string Attribution,
	int Quota,
	SourceFile[] Files);
internal sealed record SourceFile(string Path, string Url, string Sha256);

internal static class SourceFetcher
{
	private static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
	};

	public static async Task FetchAsync(CliOptions options)
	{
		var manifest = JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(options.ManifestPath), Json)
			?? throw new InvalidDataException("Source manifest is empty.");
		if (manifest.Sources is null || manifest.Sources.Length == 0 ||
			manifest.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Name) ||
				string.IsNullOrWhiteSpace(source.Revision) || string.IsNullOrWhiteSpace(source.License) ||
				string.IsNullOrWhiteSpace(source.Attribution) || source.Quota < 0 || source.Files is null) ||
			manifest.Sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length)
			throw new InvalidDataException("Source manifest metadata is incomplete or duplicated.");
		foreach (var source in manifest.Sources)
		foreach (var file in source.Files)
			if (file is null || string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Url) ||
				!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
				file.Sha256.Length != 64 || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
				throw new InvalidDataException($"Source manifest file metadata is invalid for {source.Name}.");
		var paths = manifest.Sources.SelectMany(source => source.Files).Select(file => file.Path).ToArray();
		if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
			throw new InvalidDataException("Source manifest contains duplicate raw file paths.");

		var rawRoot = Path.GetFullPath(options.RawPath);
		Directory.CreateDirectory(rawRoot);
		using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Fishbrain-DataGenerator");

		foreach (var source in manifest.Sources)
		foreach (var file in source.Files)
		{
			var destination = Path.GetFullPath(Path.Combine(rawRoot, file.Path));
			var relative = Path.GetRelativePath(rawRoot, destination);
			if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
				Path.IsPathRooted(relative))
				throw new InvalidDataException($"Source path escapes the raw data directory: {file.Path}");
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			if (File.Exists(destination) && Hash(destination).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine($"VERIFIED {source.Name} {file.Path}");
				continue;
			}

			var temporary = destination + $".{Guid.NewGuid():N}.tmp";
			try
			{
				await using (var input = await client.GetStreamAsync(file.Url))
				await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					await input.CopyToAsync(output);
				var actual = Hash(temporary);
				if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException(
						$"SHA-256 mismatch for {source.Name}/{file.Path}: expected {file.Sha256}, got {actual}.");
				File.Move(temporary, destination, true);
				Console.WriteLine($"FETCHED {source.Name} {file.Path}");
			}
			finally
			{
				if (File.Exists(temporary)) File.Delete(temporary);
			}
		}
		Console.WriteLine($"RAW {Path.GetFullPath(options.RawPath)}");
	}

	private static string Hash(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
	}
}
