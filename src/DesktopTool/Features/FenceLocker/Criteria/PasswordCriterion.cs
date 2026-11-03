using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace DesktopTool.Features.FenceLocker.Criteria;

/// <summary>Opens with a password. Only a salted PBKDF2-SHA256 hash is stored, never the password.</summary>
public sealed class PasswordCriterion : LockCriterion
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultIterations = 100_000;

    public string Salt { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public int Iterations { get; set; } = DefaultIterations;

    [JsonIgnore]
    public override string UnlockType => "Password";

    public override string Describe() => "Password";

    [JsonIgnore]
    public bool HasPassword => Hash.Length > 0;

    public static PasswordCriterion Create(string password)
    {
        var criterion = new PasswordCriterion();
        criterion.SetPassword(password);
        return criterion;
    }

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        Salt = Convert.ToBase64String(salt);
        Iterations = DefaultIterations;
        Hash = Convert.ToBase64String(Derive(password, salt, Iterations));
    }

    public bool Verify(string? password)
    {
        if (password is null || !HasPassword)
            return false;
        try
        {
            var expected = Convert.FromBase64String(Hash);
            var actual = Derive(password, Convert.FromBase64String(Salt), Iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
