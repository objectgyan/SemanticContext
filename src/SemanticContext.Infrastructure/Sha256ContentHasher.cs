using System.Security.Cryptography;
using System.Text;
using SemanticContext.Contracts;

namespace SemanticContext.Infrastructure;

public sealed class Sha256ContentHasher : IContentHasher
{
    public string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

