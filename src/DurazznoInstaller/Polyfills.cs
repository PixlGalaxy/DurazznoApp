// Types newer C# features need that .NET Framework 4.8 does not have (init setters, string[a..b], ^1), so files shared
// with Durazzno App compile here unchanged.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value; // negative = from the end (~value)

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public static Index Start => new(0);
        public static Index End => new(0, fromEnd: true);
        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(value, fromEnd: true);

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        public static implicit operator Index(int value) => FromStart(value);

        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => _value;
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }
        public Index End { get; }

        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }
}
