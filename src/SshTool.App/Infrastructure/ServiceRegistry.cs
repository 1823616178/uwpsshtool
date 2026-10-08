using System;
using System.Collections.Generic;

namespace SshTool.App.Infrastructure
{
    // 极简服务定位器（D05 组合根在后续任务填充；X07 先服务 Navigation/Dialog/Logger）。
    public static class ServiceRegistry
    {
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();

        public static void Register<T>(T instance) where T : class
        {
            Services[typeof(T)] = instance ?? throw new ArgumentNullException(nameof(instance));
        }

        public static T Get<T>() where T : class
        {
            object service;
            if (!Services.TryGetValue(typeof(T), out service))
            {
                throw new InvalidOperationException("service not registered: " + typeof(T).Name);
            }
            return (T)service;
        }

        public static bool TryGet<T>(out T instance) where T : class
        {
            object service;
            if (Services.TryGetValue(typeof(T), out service))
            {
                instance = (T)service;
                return true;
            }
            instance = null;
            return false;
        }
    }
}
