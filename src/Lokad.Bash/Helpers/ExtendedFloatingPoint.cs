using System;
using System.Numerics;

namespace Lokad.Bash.Helpers;

public enum FloatingPointClass
{
    Finite,
    Infinity,
    Nan,
    NonNumber
}

/// <summary>
/// Deterministic GNU-style extended precision: 64 significand bits, normal exponents
/// from -16382 through 16383, subnormals down to 2^-16445 and round-to-nearest-even.
/// </summary>
public readonly partial record struct ExtendedFloatingPoint(
    bool Negative,
    FloatingPointClass Class,
    ulong Significand,
    int Exponent) : IComparable<ExtendedFloatingPoint>
{
    /// <summary>Uses sort's numeric classes, ordering NaNs deterministically by payload and then sign.</summary>
    public int CompareTo(ExtendedFloatingPoint other)
    {
        var comparison = Category(this).CompareTo(Category(other));
        if (comparison != 0)
            return comparison;
        if (Class == FloatingPointClass.Nan)
        {
            comparison = Significand.CompareTo(other.Significand);
            return comparison != 0 ? comparison : Negative.CompareTo(other.Negative);
        }
        if (Class != FloatingPointClass.Finite)
            return 0;

        var sign = Significand == 0 ? 0 : Negative ? -1 : 1;
        var otherSign = other.Significand == 0 ? 0 : other.Negative ? -1 : 1;
        comparison = sign.CompareTo(otherSign);
        if (comparison != 0 || sign == 0)
            return comparison;
        comparison = Exponent.CompareTo(other.Exponent);
        return (comparison != 0 ? comparison : Significand.CompareTo(other.Significand)) * sign;

        static int Category(ExtendedFloatingPoint value) => value.Class switch
        {
            FloatingPointClass.NonNumber => 0,
            FloatingPointClass.Nan => 1,
            FloatingPointClass.Infinity => value.Negative ? 2 : 4,
            _ => 3
        };
    }

    public static ExtendedFloatingPoint FromRational(
        BigInteger numerator,
        BigInteger denominator,
        bool negative,
        out bool rangeError)
    {
        rangeError = false;
        if (numerator.IsZero)
            return new ExtendedFloatingPoint(negative, FloatingPointClass.Finite, 0, 0);

        var exponent = checked((int)(numerator.GetBitLength() - denominator.GetBitLength()));
        if (CompareToPowerOfTwo(numerator, denominator, exponent) < 0)
            exponent--;

        if (exponent > 16_383)
        {
            rangeError = true;
            return new ExtendedFloatingPoint(negative, FloatingPointClass.Infinity, 0, 0);
        }

        BigInteger rounded;
        if (exponent >= -16_382)
        {
            var shift = 63 - exponent;
            rounded = shift >= 0
                ? RoundQuotient(numerator << shift, denominator)
                : RoundQuotient(numerator, denominator << -shift);
            if (rounded == BigInteger.One << 64)
            {
                rounded >>= 1;
                exponent++;
                if (exponent > 16_383)
                {
                    rangeError = true;
                    return new ExtendedFloatingPoint(negative, FloatingPointClass.Infinity, 0, 0);
                }
            }
        }
        else
        {
            rounded = RoundQuotient(numerator << 16_445, denominator);
            rangeError = true;
            exponent = -16_382;
            if (rounded.IsZero)
                return new ExtendedFloatingPoint(negative, FloatingPointClass.Finite, 0, 0);
        }

        return new ExtendedFloatingPoint(negative, FloatingPointClass.Finite, (ulong)rounded, exponent);

        static int CompareToPowerOfTwo(BigInteger numerator, BigInteger denominator, int exponent) =>
            exponent >= 0
                ? numerator.CompareTo(denominator << exponent)
                : (numerator << -exponent).CompareTo(denominator);
    }

    public static BigInteger RoundQuotient(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder << 1).CompareTo(denominator);
        if (comparison > 0 || comparison == 0 && !quotient.IsEven)
            quotient++;
        return quotient;
    }
}
