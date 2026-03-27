using System;

namespace FactoryGenerator
{
    public class BooleanInjection : IEquatable<BooleanInjection>
    {
        public BooleanInjection(bool value, string key)
        {
            Value = value;
            Key = key;
        }

        public bool Value { get; }
        public string Key { get; }

        public bool Equals(BooleanInjection? other)
        {
            if (other is null) return false;
            return Value == other.Value && Key == other.Key;
        }

        public override bool Equals(object? obj) => obj is BooleanInjection other && Equals(other);
        public override int GetHashCode() => (Value.GetHashCode() * 397) ^ Key.GetHashCode();
    }
}