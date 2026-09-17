using System;

namespace DoesTheDogDie.Statistics;

/// <summary>
/// Special functions supporting the Beta-Bernoulli confidence model: log-gamma, log-beta,
/// the regularized incomplete beta function, and its inverse (the Beta distribution quantile).
/// </summary>
internal static class BetaFunctions
{
    // Lanczos approximation coefficients, g = 7, n = 9 (standard Numerical Recipes / Wikipedia set).
    private const double LanczosG = 7.0;

    private static readonly double[] LanczosCoefficients =
    {
        0.99999999999980993,
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    };

    /// <summary>
    /// Natural logarithm of the Gamma function, via the Lanczos approximation.
    /// </summary>
    public static double LogGamma(double x)
    {
        if (x < 0.5)
        {
            // Reflection formula: Gamma(x) * Gamma(1-x) = pi / sin(pi*x)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);
        }

        x -= 1.0;
        double a = LanczosCoefficients[0];
        double t = x + LanczosG + 0.5;
        for (int i = 1; i < LanczosCoefficients.Length; i++)
        {
            a += LanczosCoefficients[i] / (x + i);
        }

        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    /// <summary>
    /// Natural logarithm of the Beta function: ln B(a, b) = ln Gamma(a) + ln Gamma(b) - ln Gamma(a+b).
    /// </summary>
    public static double LogBeta(double a, double b)
    {
        return LogGamma(a) + LogGamma(b) - LogGamma(a + b);
    }

    /// <summary>
    /// The regularized incomplete beta function I_x(a, b), the CDF of the Beta(a, b) distribution at x.
    /// </summary>
    public static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (a <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), a, "a must be positive.");
        }

        if (b <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be positive.");
        }

        if (x < 0 || x > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "x must be in [0, 1].");
        }

        if (x == 0.0)
        {
            return 0.0;
        }

        if (x == 1.0)
        {
            return 1.0;
        }

        double frontFactor = Math.Exp(
            (a * Math.Log(x)) + (b * Math.Log(1.0 - x)) - LogBeta(a, b));

        if (x > (a + 1.0) / (a + b + 2.0))
        {
            // Use the symmetry relation for faster convergence of the continued fraction.
            return 1.0 - (frontFactor * BetaContinuedFraction(b, a, 1.0 - x) / b);
        }

        return frontFactor * BetaContinuedFraction(a, b, x) / a;
    }

    /// <summary>
    /// Modified Lentz algorithm evaluation of the continued fraction used by the incomplete beta
    /// function (Numerical Recipes `betacf`).
    /// </summary>
    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 300;
        const double epsilon = 3e-16;
        const double tiny = 1e-300;

        double qab = a + b;
        double qap = a + 1.0;
        double qam = a - 1.0;

        double c = 1.0;
        double d = 1.0 - (qab * x / qap);
        if (Math.Abs(d) < tiny)
        {
            d = tiny;
        }

        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= maxIterations; m++)
        {
            double m2 = 2.0 * m;

            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + (aa * d);
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            c = 1.0 + (aa / c);
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + (aa * d);
            if (Math.Abs(d) < tiny)
            {
                d = tiny;
            }

            c = 1.0 + (aa / c);
            if (Math.Abs(c) < tiny)
            {
                c = tiny;
            }

            d = 1.0 / d;
            double delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1.0) < epsilon)
            {
                break;
            }
        }

        return h;
    }

    /// <summary>
    /// Inverse of the regularized incomplete beta function: solves I_x(a, b) = p for x,
    /// i.e. the quantile function of the Beta(a, b) distribution.
    /// </summary>
    public static double InverseRegularizedIncompleteBeta(double a, double b, double p)
    {
        if (a <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), a, "a must be positive.");
        }

        if (b <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be positive.");
        }

        if (p < 0 || p > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "p must be in [0, 1].");
        }

        if (p == 0.0)
        {
            return 0.0;
        }

        if (p == 1.0)
        {
            return 1.0;
        }

        double lo = 0.0;
        double hi = 1.0;
        double mid = 0.5;

        for (int i = 0; i < 200 && (hi - lo) >= 1e-12; i++)
        {
            mid = 0.5 * (lo + hi);
            double value = RegularizedIncompleteBeta(a, b, mid);
            if (value < p)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        mid = 0.5 * (lo + hi);

        double logBeta = LogBeta(a, b);
        for (int i = 0; i < 5; i++)
        {
            if (mid <= 0.0 || mid >= 1.0)
            {
                mid = Math.Clamp(mid, 1e-15, 1.0 - 1e-15);
                break;
            }

            double logPdf = ((a - 1.0) * Math.Log(mid)) + ((b - 1.0) * Math.Log(1.0 - mid)) - logBeta;
            double pdf = Math.Exp(logPdf);
            if (pdf <= 0.0 || double.IsNaN(pdf) || double.IsInfinity(pdf))
            {
                break;
            }

            double diff = RegularizedIncompleteBeta(a, b, mid) - p;
            double next = mid - (diff / pdf);
            next = Math.Clamp(next, 1e-15, 1.0 - 1e-15);
            if (Math.Abs(next - mid) < 1e-15)
            {
                mid = next;
                break;
            }

            mid = next;
        }

        return mid;
    }
}
