namespace FactoryGenerator
{
    public class BooleanInjection
    {
        public BooleanInjection(bool value, string key)
        {
            Value = value;
            Key = key;
        }

        public bool Value { get; }
        public string Key { get; }
    }
}