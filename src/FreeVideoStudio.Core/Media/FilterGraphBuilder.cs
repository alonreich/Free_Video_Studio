using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

public abstract class FilterNode
{
    public abstract string ToFFmpegString();
}

public class RawFilterNode : FilterNode
{
    private readonly string _rawFilter;
    public RawFilterNode(string rawFilter) { _rawFilter = rawFilter; }
    public override string ToFFmpegString() => _rawFilter;
}

public class PadFilterNode : FilterNode
{
    public string Width { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;
    public string X { get; init; } = string.Empty;
    public string Y { get; init; } = string.Empty;
    public string Color { get; init; } = "black";

    public override string ToFFmpegString() => $"pad={Width}:{Height}:{X}:{Y}:color={Color}";
}

public class CropFilterNode : FilterNode
{
    public string Width { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;
    public string X { get; init; } = string.Empty;
    public string Y { get; init; } = string.Empty;

    public override string ToFFmpegString() => $"crop=w='{Width}':h='{Height}':x='{X}':y='{Y}'";
}

public class CasFilterNode : FilterNode
{
    public double Strength { get; init; }
    public override string ToFFmpegString() => $"cas={Strength.ToString(CultureInfo.InvariantCulture)}";
}

public class ScaleFilterNode : FilterNode
{
    public string Width { get; init; } = string.Empty;
    public string Height { get; init; } = string.Empty;

    public override string ToFFmpegString() => $"scale={Width}:{Height}";
}

public class SidechainCompressNode : FilterNode
{
    public const double TunedThreshold = 0.1;
    public const double TunedRatio = 4.0;
    public const double TunedAttackMs = 10;
    public const double TunedReleaseMs = 400;
    public const string TunedDetection = "peak";

    public const double CarveThreshold = 0.1;
    public const double CarveRatio = 2.5;
    public const double CarveAttackMs = 10;
    public const double CarveReleaseMs = 250;

    public const double BypassThreshold = 1.0;
    public const double BypassRatio = 1.0;

    public double Threshold { get; init; } = TunedThreshold;
    public double Ratio { get; init; } = TunedRatio;
    public double Attack { get; init; } = TunedAttackMs;
    public double Release { get; init; } = TunedReleaseMs;
    public string Detection { get; init; } = TunedDetection;

    public const int DefaultStrength = 50;
    public const double StrengthSpanOctaves = 1.5;

    public static double RatioFor(double tunedRatio, int strength)
    {
        int s = Math.Clamp(strength, 0, 100);
        return 1.0 + (tunedRatio - 1.0) * Math.Pow(2.0, (s - DefaultStrength) / 50.0 * StrengthSpanOctaves);
    }

    public static SidechainCompressNode Duck(int strength = DefaultStrength) => new()
    {
        Ratio = Math.Round(RatioFor(TunedRatio, strength), 3)
    };

    public static SidechainCompressNode Carve(int strength = DefaultStrength) => new()
    {
        Threshold = CarveThreshold, Ratio = Math.Round(RatioFor(CarveRatio, strength), 3),
        Attack = CarveAttackMs, Release = CarveReleaseMs
    };

    public override string ToFFmpegString() =>
        $"sidechaincompress=threshold={Threshold.ToString(CultureInfo.InvariantCulture)}:ratio={Ratio.ToString(CultureInfo.InvariantCulture)}:attack={Attack.ToString(CultureInfo.InvariantCulture)}:release={Release.ToString(CultureInfo.InvariantCulture)}:detection={Detection}";
}

public class AmixNode : FilterNode
{
    public int Inputs { get; init; } = 2;
    public string Weights { get; init; } = string.Empty;
    public int Normalize { get; init; } = 0;

    public override string ToFFmpegString()
    {
        var w = string.IsNullOrEmpty(Weights) ? "" : $":weights='{Weights}'";
        return $"amix=inputs={Inputs}{w}:normalize={Normalize}";
    }
}

public class FilterChain
{
    public List<string> InputLabels { get; } = new();
    public List<FilterNode> Nodes { get; } = new();
    public List<string> OutputLabels { get; } = new();

    public FilterChain AddNode(FilterNode node)
    {
        Nodes.Add(node);
        return this;
    }

    public FilterChain AddRaw(string raw)
    {
        Nodes.Add(new RawFilterNode(raw));
        return this;
    }

    public FilterChain WithInputs(params string[] inputs)
    {
        InputLabels.AddRange(inputs);
        return this;
    }

    public FilterChain WithOutputs(params string[] outputs)
    {
        OutputLabels.AddRange(outputs);
        return this;
    }

    public string ToFFmpegString()
    {
        string inputs = string.Join("", InputLabels.Select(l => l.StartsWith("[") ? l : $"[{l}]"));
        string outputs = string.Join("", OutputLabels.Select(l => l.StartsWith("[") ? l : $"[{l}]"));
        string filters = string.Join(",", Nodes.Select(n => n.ToFFmpegString()));
        return $"{inputs}{filters}{outputs}";
    }
}
