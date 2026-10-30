using GraphFlow.models;
using GraphFlow.flow;

namespace GraphFlow.Hosting.Demo.Models
{
    public class ExampleGraphStateObject
    {
        public Dictionary<string, string> NodeOutputs { get; set; } = new();
        public int NodeCount { get; set; }
        public bool ShouldDoTheThing { get; set; }

        public static ExampleGraphStateObject DefaultValue()
        {
            return new ExampleGraphStateObject
            {
                NodeCount = 0,
                ShouldDoTheThing = false,
                NodeOutputs = new Dictionary<string, string>()
            };
        }
    }
}


