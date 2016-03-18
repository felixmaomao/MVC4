// Copyright (c) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web.Mvc.Properties;

namespace System.Web.Mvc
{
    public class DependencyResolver
    {
        private static DependencyResolver _instance = new DependencyResolver();

        private IDependencyResolver _current;

        /// <summary>
        /// Cache should always be a new CacheDependencyResolver(_current).
        /// </summary>
        private CacheDependencyResolver _currentCache;

        public DependencyResolver()
        {
            InnerSetResolver(new DefaultDependencyResolver());
        }

        public static IDependencyResolver Current
        {
            get { return _instance.InnerCurrent; }
        }

        internal static IDependencyResolver CurrentCache
        {
            get { return _instance.InnerCurrentCache; }
        }

        public IDependencyResolver InnerCurrent
        {
            get { return _current; }
        }

        /// <summary>
        /// Provides caching over results returned by Current.
        /// </summary>
        internal IDependencyResolver InnerCurrentCache
        {
            get { return _currentCache; }
        }

        public static void SetResolver(IDependencyResolver resolver)
        {
            _instance.InnerSetResolver(resolver);
        }

        public static void SetResolver(object commonServiceLocator)
        {
            _instance.InnerSetResolver(commonServiceLocator);
        }

        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "This is an appropriate nesting of generic types.")]
        public static void SetResolver(Func<Type, object> getService, Func<Type, IEnumerable<object>> getServices)
        {
            _instance.InnerSetResolver(getService, getServices);
        }

        public void InnerSetResolver(IDependencyResolver resolver)
        {
            if (resolver == null)
            {
                throw new ArgumentNullException("resolver");
            }

            _current = resolver;
            _currentCache = new CacheDependencyResolver(_current);
        }

        public void InnerSetResolver(object commonServiceLocator)
        {
            if (commonServiceLocator == null)
            {
                throw new ArgumentNullException("commonServiceLocator");
            }

            Type locatorType = commonServiceLocator.GetType();
            MethodInfo getInstance = locatorType.GetMethod("GetInstance", new[] { typeof(Type) });
            MethodInfo getInstances = locatorType.GetMethod("GetAllInstances", new[] { typeof(Type) });

            if (getInstance == null ||
                getInstance.ReturnType != typeof(object) ||
                getInstances == null ||
                getInstances.ReturnType != typeof(IEnumerable<object>))
            {
                throw new ArgumentException(
                    String.Format(
                        CultureInfo.CurrentCulture,
                        MvcResources.DependencyResolver_DoesNotImplementICommonServiceLocator,
                        locatorType.FullName),
                    "commonServiceLocator");
            }

            var getService = (Func<Type, object>)Delegate.CreateDelegate(typeof(Func<Type, object>), commonServiceLocator, getInstance);
            var getServices = (Func<Type, IEnumerable<object>>)Delegate.CreateDelegate(typeof(Func<Type, IEnumerable<object>>), commonServiceLocator, getInstances);

            InnerSetResolver(new DelegateBasedDependencyResolver(getService, getServices));
        }

        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "This is an appropriate nesting of generic types.")]
        public void InnerSetResolver(Func<Type, object> getService, Func<Type, IEnumerable<object>> getServices)
        {
            if (getService == null)
            {
                throw new ArgumentNullException("getService");
            }
            if (getServices == null)
            {
                throw new ArgumentNullException("getServices");
            }

            InnerSetResolver(new DelegateBasedDependencyResolver(getService, getServices));
        }

        /// <summary>
        /// Wraps an IDependencyResolver and ensures single instance per-type.
        /// </summary>
        /// <remarks>
        /// Note it's possible for multiple threads to race and call the _resolver service multiple times.
        /// We'll pick one winner and ignore the others and still guarantee a unique instance.
        /// </remarks>
        
            //下面是框架内部针对DependencyResolver的几种实现 ，第一个带缓存的， 我想说 惊为天人好吗？

        private sealed class CacheDependencyResolver : IDependencyResolver
        {
            private readonly ConcurrentDictionary<Type, object> _cache = new ConcurrentDictionary<Type, object>();
            private readonly ConcurrentDictionary<Type, IEnumerable<object>> _cacheMultiple = new ConcurrentDictionary<Type, IEnumerable<object>>();

            private readonly IDependencyResolver _resolver;

            public CacheDependencyResolver(IDependencyResolver resolver)
            {
                _resolver = resolver;
            }

            public object GetService(Type serviceType)
            {
                return _cache.GetOrAdd(serviceType, _resolver.GetService);
            }

            public IEnumerable<object> GetServices(Type serviceType)
            {
                return _cacheMultiple.GetOrAdd(serviceType, _resolver.GetServices);
            }
        }

        //这个default实现竟然直接是activator.createInstance ....囧。 不过这也确实解释了 为什么我们要用外界IOC容器来创建类似 UnityResolver Ninjectresolver了。。因为框架没有帮你实现哈哈
        //突然发现 其实研究源码 从1.0版本依次往后研究才是有意思的。才能发现此中的各种改进。 也就是粗读十个源码 不如研究一个源码的十个版本。
        //针对controller这一块的扩展。 一种是全盘换掉 controllerFactory，实现自定义工厂来接入功能。 如IOC
        //也可以沿用DefautControllerFactory， 只针对 通过IdependencyResolver创建controller实例这一块来接入IOC进行拓展。
        //现在看来，更换整个工厂太兴师动众？不遇到什么超大改动应该不需要全盘更换。 一些添加缓存功能，添加日志功能应该只需要创建 controller子类就可以实现。
        //读框架源代码的意义在于，完全清楚内部运行机制，用起来更轻松，扩展也更轻松。 读应用源代码的意义多在于学习一些优秀的模块写法。
        private class DefaultDependencyResolver : IDependencyResolver
        {
            [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "This method might throw exceptions whose type we cannot strongly link against; namely, ActivationException from common service locator")]
            public object GetService(Type serviceType)
            {
                // Since attempting to create an instance of an interface or an abstract type results in an exception, immediately return null
                // to improve performance and the debugging experience with first-chance exceptions enabled.
                if (serviceType.IsInterface || serviceType.IsAbstract)
                {
                    return null;
                }

                try
                {
                    return Activator.CreateInstance(serviceType);
                }
                catch
                {
                    return null;
                }
            }

            public IEnumerable<object> GetServices(Type serviceType)
            {
                return Enumerable.Empty<object>();
            }
        }

        private class DelegateBasedDependencyResolver : IDependencyResolver
        {
            private Func<Type, object> _getService;
            private Func<Type, IEnumerable<object>> _getServices;

            public DelegateBasedDependencyResolver(Func<Type, object> getService, Func<Type, IEnumerable<object>> getServices)
            {
                _getService = getService;
                _getServices = getServices;
            }

            [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "This method might throw exceptions whose type we cannot strongly link against; namely, ActivationException from common service locator")]
            public object GetService(Type type)
            {
                try
                {
                    return _getService.Invoke(type);
                }
                catch
                {
                    return null;
                }
            }

            public IEnumerable<object> GetServices(Type type)
            {
                return _getServices(type);
            }
        }
    }
}
