﻿using System;
using System.Fabric;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Practices.Unity;
using Microsoft.ServiceFabric.Actors.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace SInnovations.ServiceFabric.Unity
{
    public class UnityServiceScope : IServiceScope
    {
        private readonly IUnityContainer container;
        private bool _containerOwner;
        public UnityServiceScope(IUnityContainer container, bool containerOwner = false)
        {
            this.container = container;
            this._containerOwner = containerOwner;
        }
        
        IServiceProvider IServiceScope.ServiceProvider => this.container.Resolve<IServiceProvider>();

       
        public void Dispose()
        {
         
            if (_containerOwner)
            {
                _containerOwner = false;
                this.container.Dispose();
            }
        }        
    }

    public class UnityServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IUnityContainer container;
        public UnityServiceScopeFactory(IUnityContainer container)
        {
            this.container = container;
        }
        public IServiceScope CreateScope()
        {
            return new UnityServiceScope(container.CreateChildContainer(),true);
           
           // return new UnityIServiceScope { ServiceProvider = new UnityServiceProvider(container.CreateChildContainer()) };
        }
    }
    public class UnityServiceProvider : IServiceProvider
    {
        private readonly IUnityContainer _container;
        public UnityServiceProvider(IUnityContainer container)
        {
            _container = container;
            
        }

        public object GetService(Type serviceType) => _container.Resolve(serviceType);

    }


    /// <summary>
    /// The <see cref="IActorDeactivationInterception"/> interface for defining an OnDeactivateInterception
    /// </summary>
    public interface IActorDeactivationInterception
    {
        void Intercept();
    }

    public class OnActorDeactivateInterceptor : IActorDeactivationInterception
    {
        private readonly IUnityContainer container;
        public OnActorDeactivateInterceptor(IUnityContainer container)
        {
            this.container = container;
        }

        public void Intercept()
        {
            this.container.Dispose();
        }
    }
    public class ActorProxyTypeFactory
    {
        /// <summary>
        /// Creates a new instance of the <see cref="ActorProxyTypeFactory"/> class.
        /// </summary>
        /// <param name="target"></param>
        public ActorProxyTypeFactory(Type target)
        {
            this.target = target;
        }

        /// <summary>
        /// Creates the proxy registered with specific interceptor.
        /// </summary>
        /// <returns></returns>
        public static T Create<T>(IActorDeactivationInterception deactivation, params object[] args)
        {
            return (T)new ActorProxyTypeFactory(typeof(T)).Create(new object[] { deactivation }.Concat(args).ToArray());
        }
        public static Type CreateType<T>()
        {
            return new ActorProxyTypeFactory(typeof(T)).CreateType();
        }
        /// <summary>
        /// Creates the proxy registered with specific interceptor.
        /// </summary>
        /// <returns></returns>
        public object Create(object[] args)
        {
            BuidAssembly();
            BuildType();
            InterceptAllMethods();

            Type proxy = this.typeBuilder.CreateType();

            return Activator.CreateInstance(proxy, args);
        }

        public Type CreateType()
        {
            BuidAssembly();
            BuildType();
            InterceptAllMethods();

            Type proxy = this.typeBuilder.CreateType();
            return proxy;
            //  return Activator.CreateInstance(proxy, args);
        }

        /// <summary>
        /// Builds a dynamic assembly with <see cref="AssemblyBuilderAccess.RunAndSave"/> mode.
        /// </summary>
        /// <returns></returns>
        public void BuidAssembly()
        {
            AssemblyName assemblyName = new AssemblyName("BasicProxy");
            AssemblyBuilder createdAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            // define module
            this.moduleBuilder = createdAssembly.DefineDynamicModule(assemblyName.Name);
        }

        public void BuildType()
        {
            if (!target.IsPublic)
            {
                throw new ArgumentException("Actors have to be public defined to proxy them");
            }


            this.typeBuilder =
                this.moduleBuilder.DefineType(target.FullName + "Proxy", TypeAttributes.Class | TypeAttributes.Public, target);
            this.fldInterceptor = this.typeBuilder.DefineField("interceptor", typeof(IActorDeactivationInterception), FieldAttributes.Private);

            foreach (var constructor in target.GetConstructors())
            {
                //  Type[] parameters = new Type[1];

                ParameterInfo[] parameterInfos = constructor.GetParameters();
                Type[] parameters = new Type[parameterInfos.Length + 1];

                parameters[0] = typeof(IActorDeactivationInterception);


                for (int index = 1; index <= parameterInfos.Length; index++)
                {
                    parameters[index] = parameterInfos[index - 1].ParameterType;
                }

                ConstructorBuilder constructorBuilder =
                    typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameters);

                for (int argumentIndex = 0; argumentIndex < parameters.Length; argumentIndex++)
                    constructorBuilder.DefineParameter(
                        argumentIndex + 1,
                        ParameterAttributes.None,
                        $"arg{argumentIndex}");

                ILGenerator generator = constructorBuilder.GetILGenerator();

                generator.Emit(OpCodes.Ldarg_0);

                for (int index = 1; index < parameters.Length; index++)
                {
                    generator.Emit(OpCodes.Ldarg, index + 1);
                }

                generator.Emit(OpCodes.Call, constructor);

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Stfld, fldInterceptor);
                generator.Emit(OpCodes.Ret);
            }
        }

        /// <summary>
        /// Builds a type in the dynamic assembly, if already the type is not created.
        /// </summary>
        /// <returns></returns>
        public void InterceptAllMethods()
        {

            const MethodAttributes targetMethodAttributes =
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;

            var methodInfo = target.GetMethod("OnDeactivateAsync", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            {
                if (methodInfo.IsVirtual)
                {
                    Type[] paramTypes = GetParameterTypes(methodInfo.GetParameters());

                    MethodBuilder methodBuilder =
                        typeBuilder.DefineMethod(methodInfo.Name, targetMethodAttributes, methodInfo.ReturnType, paramTypes);

                    ILGenerator ilGenerator = methodBuilder.GetILGenerator();


                    ilGenerator.Emit(OpCodes.Ldarg_0);
                    ilGenerator.Emit(OpCodes.Ldfld, fldInterceptor);
                    ilGenerator.Emit(OpCodes.Call, typeof(IActorDeactivationInterception).GetMethod("Intercept"));

                    ilGenerator.Emit(OpCodes.Ldarg_0);
                    ilGenerator.Emit(OpCodes.Call, methodInfo);
                    ilGenerator.Emit(OpCodes.Ret);

                    return;


                }
            }
        }



        private Type[] GetParameterTypes(ParameterInfo[] parameterInfos)
        {
            Type[] parameters = new Type[parameterInfos.Length];

            int index = 0;

            foreach (var parameterInfo in parameterInfos)
            {
                parameters[index++] = parameterInfo.ParameterType;
            }
            return parameters;
        }

        private TypeBuilder typeBuilder;
        private ModuleBuilder moduleBuilder;
        private readonly Type target;
        private FieldInfo fldInterceptor;

    }

    public static class UnityFabricExtensions
    {
        public static IUnityContainer AsFabricContainer(this IUnityContainer container)
        {
            return container.AsFabricContainer(c => FabricRuntime.Create());
        }
        public static IUnityContainer AsFabricContainer(this IUnityContainer container, Func<IUnityContainer, FabricRuntime> factory)
        {
            container.RegisterType<FabricRuntime>(new ContainerControlledLifetimeManager(), new InjectionFactory(factory));
            container.RegisterType<IServiceScopeFactory, UnityServiceScopeFactory>(new HierarchicalLifetimeManager());
            container.RegisterType<IServiceScope, UnityServiceScope>(new HierarchicalLifetimeManager());
            container.RegisterType<IServiceProvider, UnityServiceProvider>(new HierarchicalLifetimeManager());


            return container;
        }


        public static IUnityContainer WithActor<TActor>(this IUnityContainer container, ActorServiceSettings settings = null) where TActor : ActorBase
        {
            var logger = container.Resolve<ILoggerFactory>().CreateLogger<TActor>();
            logger.LogInformation("Registering Actor {ActorName}", typeof(TActor).Name);

            if (!container.IsRegistered<IActorDeactivationInterception>())
            {
                container.RegisterType<IActorDeactivationInterception, OnActorDeactivateInterceptor>(new HierarchicalLifetimeManager());
            }

            container.RegisterType(typeof(TActor), ActorProxyTypeFactory.CreateType<TActor>(), new HierarchicalLifetimeManager());
            ActorRuntime.RegisterActorAsync<TActor>((context, actorType) => {
                try
                {
                    return new ActorService(context,
                        actorTypeInfo: actorType,
                        actorFactory: (service, id) =>
                             container.CreateChildContainer()
                                 .RegisterInstance(service, new ContainerControlledLifetimeManager())
                                 .RegisterInstance(id, new ContainerControlledLifetimeManager()).Resolve<TActor>(),
                        settings: settings);
                }
                catch (Exception ex)
                {
                    logger.LogCritical(new EventId(100,"FailedToCreateActorService"), ex,"Failed to create ActorService for {ActorName}", typeof(TActor).Name);
                    throw;
                }
            }).GetAwaiter().GetResult();




            return container;
        }

        public static IUnityContainer WithStatelessService<TStatelessService>(this IUnityContainer container, string serviceTypeName, TimeSpan timeout=default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken)) where TStatelessService : StatelessService
        {
            ServiceRuntime.RegisterServiceAsync(serviceTypeName, (context) => MakeServiceContainer(container, context).Resolve<TStatelessService>(), timeout, cancellationToken).GetAwaiter().GetResult();
            return container;
        }
        public static IUnityContainer WithStatefullService<TStatelessService>(this IUnityContainer container, string serviceTypeName, TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken)) where TStatelessService : StatefulService
        {
            ServiceRuntime.RegisterServiceAsync(serviceTypeName, (context) => MakeServiceContainer(container, context).Resolve<TStatelessService>(), timeout, cancellationToken).GetAwaiter().GetResult();
            return container;
        }

        private static IUnityContainer MakeServiceContainer<T>(IUnityContainer container, T context) where T : ServiceContext
        {
            var child = container.CreateChildContainer();
            child.RegisterInstance<ServiceContext>(context, new ContainerControlledLifetimeManager());
            child.RegisterInstance(context, new ContainerControlledLifetimeManager());

            return child;
        }
         
    }
}
