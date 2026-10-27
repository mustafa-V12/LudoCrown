using System.Security.Cryptography;

namespace LudoCrown.Core.Dice;

/// <summary>Source of die values (1..6). Injected so tests are deterministic and the server controls dice.</summary>
public interface IDiceRoller
{
    int Roll();
}

/// <summary>Unbiased cryptographic dice. Used by the online server and for offline play.</summary>
public sealed class CryptoDiceRoller : IDiceRoller
{
    public int Roll() => RandomNumberGenerator.GetInt32(1, 7);
}

/// <summary>Reproducible dice from a seed (for simulations and AI testing).</summary>
public sealed class SeededDiceRoller(int seed) : IDiceRoller
{
    private readonly Random _random = new(seed);
    public int Roll() => _random.Next(1, 7);
}

/// <summary>Returns a fixed script of values; throws when exhausted. For tests.</summary>
public sealed class ScriptedDiceRoller(params int[] values) : IDiceRoller
{
    private readonly Queue<int> _values = new(values);

    public int Remaining => _values.Count;

    public int Roll()
    {
        if (_values.Count == 0) throw new InvalidOperationException("Scripted dice exhausted.");
        var v = _values.Dequeue();
        if (v is < 1 or > 6) throw new InvalidOperationException($"Invalid scripted die value {v}.");
        return v;
    }
}
