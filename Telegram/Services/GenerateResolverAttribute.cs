using System;

namespace Telegram.Services
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class GenerateResolverAttribute : Attribute
    {
        public Type[] Self { get; set; }
        public Type[] Globals { get; set; }
        public Type[] Exposed { get; set; }
        public Type[] Singletons { get; set; }
        public Type[] Lazy { get; set; }
        public Type[] Instances { get; set; }
    }
}
