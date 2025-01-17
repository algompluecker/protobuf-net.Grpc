﻿using System;
using System.Collections.Generic;
using System.Reflection;
using Grpc.Core;
using ProtoBuf.Grpc.Configuration;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace ProtoBuf.Grpc.Internal
{
    internal readonly struct ContractOperation
    {
        public string Name { get; }
        public Type From { get; }
        public Type To { get; }
        public MethodInfo Method { get; }
        public MethodType MethodType { get; }
        public ContextKind Context { get; }
        public ResultKind Arg { get; }
        public ResultKind Result { get; }
        public VoidKind Void { get; }
        public bool VoidRequest => (Void & VoidKind.Request) != 0;
        public bool VoidResponse => (Void & VoidKind.Response) != 0;

        public override string ToString() => $"{Name}: {From.Name}=>{To.Name}, {MethodType}, {Result}, {Context}, {Void}";

        public ContractOperation(string name, Type from, Type to, MethodInfo method,
            MethodType methodType, ContextKind contextKind, ResultKind arg, ResultKind resultKind, VoidKind @void)
        {
            Name = name;
            From = from;
            To = to;
            Method = method;
            MethodType = methodType;
            Context = contextKind;
            Arg = arg;
            Result = resultKind;
            Void = @void;
        }

        internal enum TypeCategory
        {
            None,
            Void,
            UntypedTask,
            UntypedValueTask,
            TypedTask,
            TypedValueTask,
            IAsyncEnumerable,
            IAsyncStreamReader,
            IServerStreamWriter,
            IObservable,
            CallOptions,
            ServerCallContext,
            CallContext,
            CancellationToken,
            AsyncUnaryCall,
            AsyncClientStreamingCall,
            AsyncDuplexStreamingCall,
            AsyncServerStreamingCall,
            Data,
            Invalid,
        }

        const int RET = -1, VOID = -2;
        private static readonly Dictionary<(TypeCategory Arg0, TypeCategory Arg1, TypeCategory Arg2, TypeCategory Ret), (ContextKind Context, MethodType Method, ResultKind Arg, ResultKind Result, VoidKind Void, int From, int To)>
            s_signaturePatterns = new Dictionary<(TypeCategory, TypeCategory, TypeCategory, TypeCategory), (ContextKind, MethodType, ResultKind, ResultKind, VoidKind, int, int)>
        {
                // google server APIs
                { (TypeCategory.IAsyncStreamReader, TypeCategory.ServerCallContext, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.ServerCallContext, MethodType.ClientStreaming, ResultKind.Grpc, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncStreamReader, TypeCategory.IServerStreamWriter, TypeCategory.ServerCallContext, TypeCategory.UntypedTask), (ContextKind.ServerCallContext, MethodType.DuplexStreaming, ResultKind.Grpc, ResultKind.Task, VoidKind.None, 0, 1) },
                { (TypeCategory.Data, TypeCategory.IServerStreamWriter, TypeCategory.ServerCallContext, TypeCategory.UntypedTask), (ContextKind.ServerCallContext, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Task, VoidKind.None, 0, 1) },
                { (TypeCategory.Data, TypeCategory.ServerCallContext, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.ServerCallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.None, 0, RET) },

                // google client APIs
                { (TypeCategory.Data, TypeCategory.CallOptions, TypeCategory.None, TypeCategory.AsyncUnaryCall), (ContextKind.CallOptions, MethodType.Unary, ResultKind.Sync, ResultKind.Grpc, VoidKind.None, 0, RET) },
                { (TypeCategory.CallOptions, TypeCategory.None, TypeCategory.None, TypeCategory.AsyncClientStreamingCall), (ContextKind.CallOptions, MethodType.ClientStreaming, ResultKind.Grpc, ResultKind.Grpc, VoidKind.None, RET, RET) },
                { (TypeCategory.CallOptions, TypeCategory.None, TypeCategory.None, TypeCategory.AsyncDuplexStreamingCall), (ContextKind.CallOptions, MethodType.DuplexStreaming, ResultKind.Grpc, ResultKind.Grpc, VoidKind.None, RET, RET) },
                { (TypeCategory.Data, TypeCategory.CallOptions, TypeCategory.None, TypeCategory.AsyncServerStreamingCall), (ContextKind.CallOptions, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Grpc, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CallOptions, TypeCategory.None, TypeCategory.Data), (ContextKind.CallOptions, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.None, 0, RET) },

                // unary parameterless, with or without a return value
                { (TypeCategory.None,TypeCategory.None, TypeCategory.None, TypeCategory.Void), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Both, VOID, VOID)},
                { (TypeCategory.None,TypeCategory.None, TypeCategory.None, TypeCategory.Data), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Request, VOID, RET)},
                { (TypeCategory.None,TypeCategory.None, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Both, VOID, VOID) },
                { (TypeCategory.None,TypeCategory.None, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Both, VOID, VOID) },
                { (TypeCategory.None,TypeCategory.None, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Request, VOID, RET) },
                { (TypeCategory.None,TypeCategory.None, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Request, VOID, RET) },

                { (TypeCategory.CallContext,TypeCategory.None, TypeCategory.None, TypeCategory.Void), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Both,VOID, VOID)},
                { (TypeCategory.CallContext,TypeCategory.None, TypeCategory.None, TypeCategory.Data), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Request, VOID, RET)},
                { (TypeCategory.CallContext,TypeCategory.None, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Both, VOID, VOID) },
                { (TypeCategory.CallContext,TypeCategory.None, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Both,VOID, VOID) },
                { (TypeCategory.CallContext,TypeCategory.None, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Request, VOID, RET) },
                { (TypeCategory.CallContext,TypeCategory.None, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Request, VOID, RET) },

                { (TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.None, TypeCategory.Void), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Both,VOID, VOID)},
                { (TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.None, TypeCategory.Data), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Request, VOID, RET)},
                { (TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Both, VOID, VOID) },
                { (TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Both,VOID, VOID) },
                { (TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Request, VOID, RET) },
                { (TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Request, VOID, RET) },

                // unary with parameter, with or without a return value
                { (TypeCategory.Data, TypeCategory.None,TypeCategory.None, TypeCategory.Void), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Response, 0, VOID)},
                { (TypeCategory.Data, TypeCategory.None,TypeCategory.None, TypeCategory.Data), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.None, 0, RET)},
                { (TypeCategory.Data, TypeCategory.None,TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Response, 0, VOID) },
                { (TypeCategory.Data, TypeCategory.None,TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.Data, TypeCategory.None,TypeCategory.None, TypeCategory.TypedTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.None,TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.NoContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.None, 0, RET) },

                { (TypeCategory.Data, TypeCategory.CallContext,TypeCategory.None, TypeCategory.Void), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Response,0, VOID)},
                { (TypeCategory.Data, TypeCategory.CallContext,TypeCategory.None, TypeCategory.Data), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.None, 0, RET)},
                { (TypeCategory.Data, TypeCategory.CallContext,TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Response, 0, VOID) },
                { (TypeCategory.Data, TypeCategory.CallContext,TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Response,0, VOID) },
                { (TypeCategory.Data, TypeCategory.CallContext,TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CallContext,TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CallContext, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.None, 0, RET) },

                { (TypeCategory.Data, TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.Void), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Response,0, VOID)},
                { (TypeCategory.Data, TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.Data), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.None, 0, RET)},
                { (TypeCategory.Data, TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Response, 0, VOID) },
                { (TypeCategory.Data, TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Response,0, VOID) },
                { (TypeCategory.Data, TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CancellationToken,TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CancellationToken, MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.None, 0, RET) },

                // client streaming
                { (TypeCategory.IAsyncEnumerable, TypeCategory.None, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.None, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.None, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.None, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.Response, 0, VOID) },

                { (TypeCategory.IAsyncEnumerable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.Response, 0, VOID) },

                { (TypeCategory.IAsyncEnumerable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.Response, 0, VOID) },

                // (and for observable)
                { (TypeCategory.IObservable, TypeCategory.None, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.None, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.IObservable, TypeCategory.None, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.None, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.NoContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.Response, 0, VOID) },

                { (TypeCategory.IObservable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.IObservable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CallContext, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.Response, 0, VOID) },

                { (TypeCategory.IObservable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.TypedValueTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.UntypedValueTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.Response, 0, VOID) },
                { (TypeCategory.IObservable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.TypedTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.UntypedTask), (ContextKind.CancellationToken, MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.Response, 0, VOID) },

                // server streaming
                { (TypeCategory.None, TypeCategory.None, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.NoContext, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.AsyncEnumerable, VoidKind.Request, VOID, RET) },
                { (TypeCategory.CallContext, TypeCategory.None, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.CallContext, MethodType.ServerStreaming, ResultKind.Sync,ResultKind.AsyncEnumerable, VoidKind.Request, VOID, RET) },
                { (TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.CancellationToken, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.AsyncEnumerable, VoidKind.Request, VOID, RET) },
                { (TypeCategory.Data, TypeCategory.None, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.NoContext, MethodType.ServerStreaming, ResultKind.Sync,ResultKind.AsyncEnumerable, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CallContext, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.CallContext, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.AsyncEnumerable, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.CancellationToken, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.AsyncEnumerable, VoidKind.None, 0, RET) },

                // (and for observable)
                { (TypeCategory.None, TypeCategory.None, TypeCategory.None, TypeCategory.IObservable), (ContextKind.NoContext, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Observable, VoidKind.Request, VOID, RET) },
                { (TypeCategory.CallContext, TypeCategory.None, TypeCategory.None, TypeCategory.IObservable), (ContextKind.CallContext, MethodType.ServerStreaming, ResultKind.Sync,ResultKind.Observable, VoidKind.Request, VOID, RET) },
                { (TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.None, TypeCategory.IObservable), (ContextKind.CancellationToken, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Observable, VoidKind.Request, VOID, RET) },
                { (TypeCategory.Data, TypeCategory.None, TypeCategory.None, TypeCategory.IObservable), (ContextKind.NoContext, MethodType.ServerStreaming, ResultKind.Sync,ResultKind.Observable, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CallContext, TypeCategory.None, TypeCategory.IObservable), (ContextKind.CallContext, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Observable, VoidKind.None, 0, RET) },
                { (TypeCategory.Data, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.IObservable), (ContextKind.CancellationToken, MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Observable, VoidKind.None, 0, RET) },

                // duplex
                { (TypeCategory.IAsyncEnumerable, TypeCategory.None, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.NoContext, MethodType.DuplexStreaming, ResultKind.AsyncEnumerable,ResultKind.AsyncEnumerable, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.CallContext, MethodType.DuplexStreaming, ResultKind.AsyncEnumerable, ResultKind.AsyncEnumerable, VoidKind.None, 0, RET) },
                { (TypeCategory.IAsyncEnumerable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.IAsyncEnumerable), (ContextKind.CancellationToken, MethodType.DuplexStreaming, ResultKind.AsyncEnumerable, ResultKind.AsyncEnumerable, VoidKind.None, 0, RET) },

                // (and for observable)
                { (TypeCategory.IObservable, TypeCategory.None, TypeCategory.None, TypeCategory.IObservable), (ContextKind.NoContext, MethodType.DuplexStreaming, ResultKind.Observable,ResultKind.Observable, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.CallContext, TypeCategory.None, TypeCategory.IObservable), (ContextKind.CallContext, MethodType.DuplexStreaming, ResultKind.Observable, ResultKind.Observable, VoidKind.None, 0, RET) },
                { (TypeCategory.IObservable, TypeCategory.CancellationToken, TypeCategory.None, TypeCategory.IObservable), (ContextKind.CancellationToken, MethodType.DuplexStreaming, ResultKind.Observable, ResultKind.Observable, VoidKind.None, 0, RET) },
        };
        internal static int SignatureCount => s_signaturePatterns.Count;

        internal static int GeneralPurposeSignatureCount() => s_signaturePatterns.Values.Count(x => x.Context == ContextKind.CallContext || x.Context == ContextKind.NoContext || x.Context == ContextKind.CancellationToken);

        static TypeCategory GetCategory(MarshallerCache marshallerCache, Type type, IBindContext? bindContext)
        {
            if (type == null) return TypeCategory.None;
            if (type == typeof(void)) return TypeCategory.Void;
            if (type == typeof(Task)) return TypeCategory.UntypedTask;
            if (type == typeof(ValueTask)) return TypeCategory.UntypedValueTask;
            if (type == typeof(ServerCallContext)) return TypeCategory.ServerCallContext;
            if (type == typeof(CallOptions)) return TypeCategory.CallOptions;
            if (type == typeof(CallContext)) return TypeCategory.CallContext;
            if (type == typeof(CancellationToken)) return TypeCategory.CancellationToken;

            if (type.IsGenericType)
            {
                var genType = type.GetGenericTypeDefinition();
                if (genType == typeof(Task<>)) return TypeCategory.TypedTask;
                if (genType == typeof(ValueTask<>)) return TypeCategory.TypedValueTask;
                if (genType == typeof(IAsyncEnumerable<>)) return TypeCategory.IAsyncEnumerable;
                if (genType == typeof(IAsyncStreamReader<>)) return TypeCategory.IAsyncStreamReader;
                if (genType == typeof(IServerStreamWriter<>)) return TypeCategory.IServerStreamWriter;
                if (genType == typeof(IObservable<>)) return TypeCategory.IObservable;
                if (genType == typeof(AsyncUnaryCall<>)) return TypeCategory.AsyncUnaryCall;
                if (genType == typeof(AsyncClientStreamingCall<,>)) return TypeCategory.AsyncClientStreamingCall;
                if (genType == typeof(AsyncDuplexStreamingCall<,>)) return TypeCategory.AsyncDuplexStreamingCall;
                if (genType == typeof(AsyncServerStreamingCall<>)) return TypeCategory.AsyncServerStreamingCall;
            }

            if (typeof(Delegate).IsAssignableFrom(type)) return TypeCategory.None; // yeah, that's not going to happen

            if (marshallerCache.CanSerializeType(type)) return TypeCategory.Data;
            bindContext?.LogWarning("Type cannot be serialized; ignoring: {0}", type.FullName);
            return TypeCategory.Invalid;
        }

        internal static (TypeCategory Arg0, TypeCategory Arg1, TypeCategory Arg2, TypeCategory Ret) GetSignature(MarshallerCache marshallerCache, MethodInfo method, IBindContext? bindContext)
            => GetSignature(marshallerCache, method.GetParameters(), method.ReturnType, bindContext);

        private static (TypeCategory Arg0, TypeCategory Arg1, TypeCategory Arg2, TypeCategory Ret) GetSignature(MarshallerCache marshallerCache, ParameterInfo[] args, Type returnType, IBindContext? bindContext)
        {
            (TypeCategory Arg0, TypeCategory Arg1, TypeCategory Arg2, TypeCategory Ret) signature = default;
            if (args.Length >= 1) signature.Arg0 = GetCategory(marshallerCache, args[0].ParameterType, bindContext);
            if (args.Length >= 2) signature.Arg1 = GetCategory(marshallerCache, args[1].ParameterType, bindContext);
            if (args.Length >= 3) signature.Arg2 = GetCategory(marshallerCache, args[2].ParameterType, bindContext);
            signature.Ret = GetCategory(marshallerCache, returnType, bindContext);
            return signature;
        }
        internal static bool TryIdentifySignature(MethodInfo method, BinderConfiguration binderConfig, out ContractOperation operation, IBindContext? bindContext)
        {
            operation = default;

            if (method.IsGenericMethodDefinition) return false; // can't work with <T> methods

            if ((method.Attributes & (MethodAttributes.SpecialName)) != 0) return false; // some kind of accessor etc

            if (!binderConfig.Binder.IsOperationContract(method, out var opName)) return false;

            var args = method.GetParameters();
            if (args.Length > 3) return false; // too many parameters

            var signature = GetSignature(binderConfig.MarshallerCache, args, method.ReturnType, bindContext);

            if (!s_signaturePatterns.TryGetValue(signature, out var config)) return false;

            (Type type, TypeCategory category) GetTypeByIndex(int index)
            {
                return index switch
                {
                    0 => (args[0].ParameterType, signature.Arg0),
                    1 => (args[1].ParameterType, signature.Arg1),
                    2 => (args[2].ParameterType, signature.Arg2),
                    RET => (method.ReturnType, signature.Ret),
                    VOID => (typeof(void), TypeCategory.Void),
                    _ => throw new IndexOutOfRangeException(nameof(index)),
                };
            }

            static Type GetDataType((Type type, TypeCategory category) key, bool req)
            {
                var type = key.type;
                switch (key.category)
                {
                    case TypeCategory.Data:
                        return type;
                    case TypeCategory.Void:
                    case TypeCategory.UntypedTask:
                    case TypeCategory.UntypedValueTask:
#pragma warning disable CS0618 // Empty
                        return typeof(Empty);
#pragma warning restore CS0618
                    case TypeCategory.TypedTask:
                    case TypeCategory.TypedValueTask:
                    case TypeCategory.IAsyncEnumerable:
                    case TypeCategory.IAsyncStreamReader:
                    case TypeCategory.IServerStreamWriter:
                    case TypeCategory.AsyncUnaryCall:
                    case TypeCategory.AsyncServerStreamingCall:
                    case TypeCategory.IObservable:
                        return type.GetGenericArguments()[0];
                    case TypeCategory.AsyncClientStreamingCall:
                    case TypeCategory.AsyncDuplexStreamingCall:
                        return type.GetGenericArguments()[req ? 0 : 1];
                    default:
                        throw new ArgumentOutOfRangeException(key.category.ToString());
                }
            }

            var from = GetDataType(GetTypeByIndex(config.From), true);
            var to = GetDataType(GetTypeByIndex(config.To), false);

            operation = new ContractOperation(opName!, from, to, method, config.Method, config.Context, config.Arg, config.Result, config.Void);
            return true;
        }
        public static List<ContractOperation> FindOperations(BinderConfiguration binderConfig, Type contractType, IBindContext? bindContext)
        {
            var all = contractType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var ops = new List<ContractOperation>(all.Length);
            foreach (var method in all)
            {
                if (method.DeclaringType == typeof(object))
                { /* skip */ }
                else if (TryIdentifySignature(method, binderConfig, out var op, bindContext))
                {
                    ops.Add(op);
                }
                else
                {
                    bindContext?.LogWarning("Signature not recognized for {0}.{1}; method will not be bound", contractType.FullName, method.Name);
                }
            }
            return ops;
        }


        internal MethodInfo? TryGetClientHelper()
        {

            var name = GetClientHelperName();
            try
            {
                if (name == null || !s_reshaper.TryGetValue(name, out var method)) return null;
                return method.MakeGenericMethod(From, To);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error obtaining client-helper '{name}' (from: '{From?.FullName}', to: '{To?.FullName}'): {ex.Message}", ex);
            }
        }
#pragma warning disable CS0618 // Reshape
        static readonly Dictionary<string, MethodInfo> s_reshaper =

            (from method in typeof(Reshape).GetMethods(BindingFlags.Public | BindingFlags.Static)
             where method.IsGenericMethodDefinition
             let parameters = method.GetParameters()
             where parameters.Length > 1
             && parameters[1].ParameterType == typeof(CallInvoker)
             && parameters[0].ParameterType == typeof(CallContext).MakeByRefType()
             select method).ToDictionary(x => x.Name);

        static readonly Dictionary<(MethodType, ResultKind, ResultKind, VoidKind), string> _clientResponseMap = new Dictionary<(MethodType, ResultKind, ResultKind, VoidKind), string>
        {
            {(MethodType.DuplexStreaming, ResultKind.AsyncEnumerable, ResultKind.AsyncEnumerable, VoidKind.None), nameof(Reshape.DuplexAsync) },
            {(MethodType.DuplexStreaming, ResultKind.Observable, ResultKind.Observable, VoidKind.None), nameof(Reshape.DuplexObservable) },
            {(MethodType.ServerStreaming, ResultKind.Sync, ResultKind.AsyncEnumerable, VoidKind.None), nameof(Reshape.ServerStreamingAsync) },
            {(MethodType.ServerStreaming, ResultKind.Sync, ResultKind.Observable, VoidKind.None), nameof(Reshape.ServerStreamingObservable) },
            {(MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.None), nameof(Reshape.ClientStreamingTaskAsync) },
            {(MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.Task, VoidKind.Response), nameof(Reshape.ClientStreamingTaskAsync) }, // Task<T> works as Task
            {(MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.None), nameof(Reshape.ClientStreamingValueTaskAsync) },
            {(MethodType.ClientStreaming, ResultKind.AsyncEnumerable, ResultKind.ValueTask, VoidKind.Response), nameof(Reshape.ClientStreamingValueTaskAsyncVoid) },
            {(MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.None), nameof(Reshape.ClientStreamingObservableTaskAsync) },
            {(MethodType.ClientStreaming, ResultKind.Observable, ResultKind.Task, VoidKind.Response), nameof(Reshape.ClientStreamingObservableTaskAsync) }, // Task<T> works as Task
            {(MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.None), nameof(Reshape.ClientStreamingObservableValueTaskAsync) },
            {(MethodType.ClientStreaming, ResultKind.Observable, ResultKind.ValueTask, VoidKind.Response), nameof(Reshape.ClientStreamingObservableValueTaskAsyncVoid) },
            {(MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.None), nameof(Reshape.UnaryTaskAsync) },
            {(MethodType.Unary, ResultKind.Sync, ResultKind.Task, VoidKind.Response), nameof(Reshape.UnaryTaskAsync) }, // Task<T> works as Task
            {(MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.None), nameof(Reshape.UnaryValueTaskAsync) },
            {(MethodType.Unary, ResultKind.Sync, ResultKind.ValueTask, VoidKind.Response), nameof(Reshape.UnaryValueTaskAsyncVoid) },
            {(MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.None), nameof(Reshape.UnarySync) },
            {(MethodType.Unary, ResultKind.Sync, ResultKind.Sync, VoidKind.Response), nameof(Reshape.UnarySyncVoid) },
        };
#pragma warning restore CS0618

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0066:Convert switch statement to expression", Justification = "Isn't actually C# 8.0 (but works on preview compiler)")]
        private string? GetClientHelperName()
        {
            switch (Context)
            {
                case ContextKind.CallContext:
                case ContextKind.NoContext:
                case ContextKind.CancellationToken:
                    return _clientResponseMap.TryGetValue((MethodType, Arg, Result, Void & VoidKind.Response), out var helper) ? helper : null;
                default:
                    return null;
            };
        }

        internal bool IsSyncT()
        {
            return Method.ReturnType == To;
        }
        internal bool IsTaskT()
        {
            var ret = Method.ReturnType;
            return ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>)
                && ret.GetGenericArguments()[0] == To;
        }
        internal bool IsValueTaskT()
        {
            var ret = Method.ReturnType;
            return ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(ValueTask<>)
                && ret.GetGenericArguments()[0] == To;
        }

        internal static ISet<Type> ExpandInterfaces(Type type)
        {
            var set = new HashSet<Type>(type.GetInterfaces());
            if (type.IsInterface) set.Add(type);
            return set;
        }

        /// <summary>
        /// Collect all the types to be used for extracting methods for a specific Service Contract
        /// </summary>
        /// <param name="serviceBinder"></param>
        /// <param name="serviceContract">Must be a service contract</param>
        /// <returns>types to be used for extracting methods</returns>
        internal static ISet<Type> ExpandWithInterfacesMarkedAsSubService(ServiceBinder serviceBinder,
            Type serviceContract)
        {
            var set = new HashSet<Type>();
            
            // first add the service contract by itself 
            set.Add(serviceContract); 

            // now add all inherited interfaces which are marked as sub-services
            foreach (var t in serviceContract.GetInterfaces())
            {
                if (t.IsDefined(typeof(SubServiceAttribute)))
                {
                    set.Add(t);
                }
            }

            ValidateServiceContracts(serviceBinder, set);
            return set;
        }

        private static void ValidateServiceContracts(ServiceBinder serviceBinder, HashSet<Type> set)
        {
            foreach (var item in set)
            {
                if (item.IsDefined(typeof(SubServiceAttribute)))
                {
                    if (serviceBinder.IsServiceContract(item, out var serviceName))
                        throw new ArgumentException(
                            $"Bad definition for service {serviceName}: " +
                            $"A service contract cannot be marked as a sub-service as well");
                }
            }
        }
    }

    internal enum ContextKind
    {
        NoContext, // no context
        CallContext, // pb-net shared context kind
        CallOptions, // GRPC core client context kind
        ServerCallContext, // GRPC core server context kind
        CancellationToken, // cancellation (without extra context)
    }

    internal enum ResultKind
    {
        Unknown,
        Sync,
        Task,
        ValueTask,
        AsyncEnumerable,
        Grpc,
        Observable,
    }

    [Flags]
    internal enum VoidKind
    {
        None = 0,
        Request = 1,
        Response = 2,
        Both = Request | Response
    }
}
