namespace DotLight;

/// <summary>
/// A damped spring, like the ones iOS uses: it moves towards its target, overshoots a little
/// and settles. Driven once per frame.
/// </summary>
sealed class Spring
{
    public double Value, Velocity, Target;
    readonly double stiffness, damping;

    public Spring(double value, double stiffness = 380, double damping = 26)
    {
        Value = Target = value;
        this.stiffness = stiffness;
        this.damping = damping; // below 2*sqrt(stiffness) (~39) gives a slight bounce
    }

    public bool Settled => Math.Abs(Value - Target) < 0.05 && Math.Abs(Velocity) < 0.5;

    /// <summary>Advances the spring by dt seconds (in small sub-steps so it stays stable).</summary>
    public void Step(double dt)
    {
        const double sub = 1.0 / 480;
        while (dt > 0)
        {
            double h = Math.Min(sub, dt);
            double force = -stiffness * (Value - Target) - damping * Velocity;
            Velocity += force * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (Settled) { Value = Target; Velocity = 0; }
    }
}
