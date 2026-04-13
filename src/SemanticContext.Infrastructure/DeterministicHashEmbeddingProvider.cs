using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace SemanticContext.Infrastructure;

public sealed class DeterministicHashEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingProviderOptions _options;

    public DeterministicHashEmbeddingProvider(IOptions<EmbeddingProviderOptions> options)
    {
        _options = options.Value;
    }

    public Task<IReadOnlyList<float>> CreateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
    {
        var dimension = Math.Max(1, _options.Dimension);
        var vector = new float[dimension];
        var tokens = Tokenize(input);

        var position = 0;
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hash = HashToUInt32(token);
            var index = (int)(hash % (uint)dimension);
            var weight = 1f + Math.Min(token.Length, 12) * 0.05f + (position % 7) * 0.01f;
            vector[index] += weight;
            position++;
        }

        Normalize(vector);
        return Task.FromResult<IReadOnlyList<float>>(vector);
    }

    private static IEnumerable<string> Tokenize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static uint HashToUInt32(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        if (sum <= 0)
        {
            return;
        }

        var magnitude = Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / magnitude);
        }
    }
}

