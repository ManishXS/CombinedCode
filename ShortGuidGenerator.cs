using System;
using System.Text;

public static class ShortGuidGenerator
{
    private const string Characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Generates a very short unique identifier.
    /// </summary>
    /// <param name="length">Length of the generated identifier (default: 6).</param>
    /// <returns>A short unique identifier as a string.</returns>
    public static string Generate(int length = 6)
    {
        if (length <= 0)
            throw new ArgumentException("Length must be greater than zero.", nameof(length));

        var stringBuilder = new StringBuilder(length);
        var random = new Random();

        for (int i = 0; i < length; i++)
        {
            stringBuilder.Append(Characters[random.Next(Characters.Length)]);
        }

        return stringBuilder.ToString();
    }
}
