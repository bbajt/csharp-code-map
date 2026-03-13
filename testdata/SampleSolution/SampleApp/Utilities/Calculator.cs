namespace SampleApp.Utilities;

/// <summary>Numeric calculator with operator overloads and indexer.</summary>
public class Calculator
{
    private readonly double[] _memory;

    public Calculator(int slots = 10)
    {
        _memory = new double[slots];
    }

    public double this[int index]
    {
        get => _memory[index];
        set => _memory[index] = value;
    }

    public double Add(double a, double b) => a + b;
    public double Subtract(double a, double b) => a - b;
    public double Multiply(double a, double b) => a * b;

    public static Calculator operator +(Calculator a, Calculator b)
    {
        var result = new Calculator(a._memory.Length);
        for (int i = 0; i < a._memory.Length; i++)
            result._memory[i] = a._memory[i] + b._memory[i];
        return result;
    }
}
