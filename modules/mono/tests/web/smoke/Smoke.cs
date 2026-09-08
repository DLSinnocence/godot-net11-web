using System;
using Godot;

public partial class Smoke : Node
{
    private const string ExpectedExceptionMarker = "SMOKE:EXPECTED_EXCEPTION";

    public override void _Ready()
    {
        try
        {
            ExerciseColorAndVariants();
            GD.Print($"SMOKE:AUDIO_DRIVER={AudioServer.GetDriverName()}");

            // Route through the generated Godot dispatcher so its exception reporter handles the stack.
            Call(MethodName.ThrowExpected);

            GD.Print("SMOKE:PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"SMOKE:FAIL\n{exception}");
            GetTree().Quit(1);
        }
    }

    private static void ThrowExpected()
    {
        throw new InvalidOperationException(ExpectedExceptionMarker);
    }

    private static void ExerciseColorAndVariants()
    {
        Color okHsl = Color.FromOkHsl(0.63f, 0.72f, 0.58f, 0.91f);
        Require(IsFinite(okHsl.R) && IsFinite(okHsl.G) && IsFinite(okHsl.B), "Color.FromOkHsl returned a non-finite channel.");
        Require(Mathf.IsEqualApprox(okHsl.A, 0.91f), "Color.FromOkHsl lost alpha.");

        using Variant colorVariant = Variant.From(okHsl);
        Require(colorVariant.VariantType == Variant.Type.Color, "Color Variant type mismatch.");
        Require(colorVariant.AsColor().IsEqualApprox(okHsl), "Color Variant round-trip mismatch.");

        RoundTripVector(new Vector2(1.25f, -2.5f));
        RoundTripVector(new Vector3(3.5f, -4.75f, 5.125f));
        RoundTripVector(new Vector4(6.25f, -7.5f, 8.75f, -9.0f));

        Color[] colors = [okHsl, new Color(0.1f, 0.2f, 0.3f, 0.4f)];
        Vector2[] vectors2 = [new(1, 2), new(3, 4)];
        Vector3[] vectors3 = [new(1, 2, 3), new(4, 5, 6)];
        Vector4[] vectors4 = [new(1, 2, 3, 4), new(5, 6, 7, 8)];

        using Variant colorsVariant = Variant.From(colors);
        using Variant vectors2Variant = Variant.From(vectors2);
        using Variant vectors3Variant = Variant.From(vectors3);
        using Variant vectors4Variant = Variant.From(vectors4);

        Require(colorsVariant.VariantType == Variant.Type.PackedColorArray, "PackedColorArray type mismatch.");
        Require(vectors2Variant.VariantType == Variant.Type.PackedVector2Array, "PackedVector2Array type mismatch.");
        Require(vectors3Variant.VariantType == Variant.Type.PackedVector3Array, "PackedVector3Array type mismatch.");
        Require(vectors4Variant.VariantType == Variant.Type.PackedVector4Array, "PackedVector4Array type mismatch.");

        Require(colorsVariant.AsColorArray().Length == colors.Length, "PackedColorArray length mismatch.");
        Require(vectors2Variant.AsVector2Array()[1].IsEqualApprox(vectors2[1]), "PackedVector2Array round-trip mismatch.");
        Require(vectors3Variant.AsVector3Array()[1].IsEqualApprox(vectors3[1]), "PackedVector3Array round-trip mismatch.");
        Require(vectors4Variant.AsVector4Array()[1].IsEqualApprox(vectors4[1]), "PackedVector4Array round-trip mismatch.");
    }

    private static void RoundTripVector(Vector2 value)
    {
        using Variant variant = Variant.From(value);
        Require(variant.AsVector2().IsEqualApprox(value), "Vector2 Variant round-trip mismatch.");
    }

    private static void RoundTripVector(Vector3 value)
    {
        using Variant variant = Variant.From(value);
        Require(variant.AsVector3().IsEqualApprox(value), "Vector3 Variant round-trip mismatch.");
    }

    private static void RoundTripVector(Vector4 value)
    {
        using Variant variant = Variant.From(value);
        Require(variant.AsVector4().IsEqualApprox(value), "Vector4 Variant round-trip mismatch.");
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
