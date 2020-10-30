// -----------------------------------------------------------------------
//   <copyright file="BroadcastGroupRouterConfig.cs" company="Asynkron AB">
//       Copyright (C) 2015-2020 Asynkron AB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

namespace Proto.Router.Routers
{
    internal record BroadcastGroupRouterConfig : GroupRouterConfig
    {
        public BroadcastGroupRouterConfig(ISenderContext senderContext, params PID[] routees) : base(senderContext,
            routees
        )
        {
        }

        public override RouterState CreateRouterState() => new BroadcastRouterState(SenderContext);
    }
}