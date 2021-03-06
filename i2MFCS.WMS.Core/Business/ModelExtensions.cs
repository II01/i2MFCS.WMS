﻿using i2MFCS.WMS.Database.DTO;
using i2MFCS.WMS.Database.Tables;
using SimpleLogs;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Linq.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace i2MFCS.WMS.Core.Business
{
    public static class ModelExtensions
    {
        public static Random Random = new Random();
        private static int _counterLast;
        private static string _destinationLast;

        public static IEnumerable<Command> ToCommands(this IEnumerable<Place> list, int orderID, string target)
        {
            foreach (Place p in list)
                yield return new Command { TU_ID = p.TU_ID, Source = p.PlaceID, Target = target, Order_ID = orderID, Status = 0, LastChange = DateTime.Now };
        }


        /// <summary>
        /// Optimised search frok SKUID,Batch,Qty order demands -> DTOCommnand
        /// </summary>
        /// <param name="dtoOrders"></param>
        /// <returns></returns>
        public static IEnumerable<DTOCommand> DTOOrderToDTOCommand(this IEnumerable<DTOOrder> dtoOrders)
        {
            using (var dc = new WMSContext())
            {
                var res = dtoOrders
                .GroupBy(
                    (by) => new { by.SKU_ID, by.SKU_Batch, by.SKU_Qty, by.QualityControlOrder },
                    (key, dtoOrderGroup) => new
                    {
                        Key = key,
                        Num = dtoOrderGroup.Count(),
                        DTOOrders = dtoOrderGroup.ToList(),
                        Place =
                           dc.TUs
                           .Where(prop => prop.SKU_ID == key.SKU_ID && prop.Batch == key.SKU_Batch && prop.Qty == key.SKU_Qty)
                           .Join(dc.Places,
                              (tu) => tu.TU_ID,
                              (place) => place.TU_ID,
                              (tu, place) => new { TU = tu, Place = place }
                            )
                            .Where(prop => prop.Place.PlaceID.StartsWith("W:")
                                          && prop.Place.FK_PlaceID.DimensionClass != 999
                                          && prop.Place.FK_PlaceID.Status == 0
                                          && ((!key.QualityControlOrder && prop.TU.FK_TU_ID.Blocked == 0) ||
                                              (key.QualityControlOrder && prop.TU.FK_TU_ID.Blocked == 4))
                                          && !dc.Commands.Any(p => p.Status < Command.CommandStatus.Canceled && 
                                                                   p.Source ==prop.Place.PlaceID && !p.Target.StartsWith("W:1") && !p.Target.StartsWith("W:2"))
                                          && !dc.Commands.Any(p => p.Status < Command.CommandStatus.Canceled &&
                                                                   p.Target.StartsWith(prop.Place.PlaceID.Substring(0,10))))
                           .OrderBy(prop => prop.TU.ExpDate)
                           .ThenByDescending(prop => prop.Place.Time)
                           .Take(dtoOrderGroup.Count())
                           .ToList()
                    })
                    .ToList();
                int logCounter = 0;
                foreach (var r in res)
                {
                    if (r.DTOOrders.Count != r.Place.Count)
                    {
                        using (var dcp = new WMSContext())
                        {
                            var cnt = dcp.Parameters.Find($"Counter[{_destinationLast}]");
                            if (cnt != null)
                            {
                                cnt.Value = Convert.ToString(_counterLast);
                                dcp.SaveChanges();
                            }
                        }
                        // check if a pallet is in warehouse but blocked due to another command
                        var Place =
                                   dc.TUs
                                   .Where(prop => prop.SKU_ID == r.Key.SKU_ID && prop.Batch == r.Key.SKU_Batch && prop.Qty == r.Key.SKU_Qty)
                                   .Join(dc.Places,
                                      (tu) => tu.TU_ID,
                                      (place) => place.TU_ID,
                                      (tu, place) => new { TU = tu, Place = place }
                                    )
                                    .Where(prop => prop.Place.PlaceID.StartsWith("W:")
                                                  && prop.Place.FK_PlaceID.DimensionClass != 999
                                                  && prop.Place.FK_PlaceID.Status == 0
                                                  && ((!r.Key.QualityControlOrder && prop.TU.FK_TU_ID.Blocked == 0) ||
                                                      (r.Key.QualityControlOrder && prop.TU.FK_TU_ID.Blocked == 4))
                                                  && !dc.Commands.Any(p => p.Status < Command.CommandStatus.Canceled &&
                                                                           p.Source == prop.Place.PlaceID && !p.Target.StartsWith("W:1") && !p.Target.StartsWith("W:2")))
                                   .OrderBy(prop => prop.TU.ExpDate)
                                   .ThenByDescending(prop => prop.Place.Time)
                                   .Take(r.Num)
                                   .ToList();
                        // check if there is a pallet on the way
                        var cmd = dc.Commands
                                    .Where(p => p.Source.StartsWith("T") && 
                                                p.Target.StartsWith("W") && !p.Target.StartsWith("W:32") &&
                                                p.Status < Command.CommandStatus.Canceled)
                                    .Join(dc.TUs,
                                          c => c.TU_ID,
                                          t => t.TU_ID,
                                          (c, t) => new { Command = c, TU = t })
                                    .Where(ct => ct.TU.SKU_ID == r.Key.SKU_ID && ct.TU.Batch == r.Key.SKU_Batch)
                                    .ToList();
                        if (r.DTOOrders.Count != Place.Count + cmd.Count)
                        {
                            for (int i = r.Place.Count; i < r.DTOOrders.Count; i++)
                            {
                                logCounter++;
                                Log.AddLog(Log.SeverityEnum.Event, nameof(DTOOrderToDTOCommand), ($"Warehouse does not have: {logCounter}: {r.DTOOrders[i].ERP_ID ?? 0}|{r.DTOOrders[i].OrderID}: ({r.Key.SKU_ID:d9}, {r.Key.SKU_Batch}) x {r.Key.SKU_Qty}"));
                                var o = dc.Orders.Find(r.DTOOrders[i].ID);
                                if (o != null)
                                {
                                    o.Status = o.Destination.StartsWith("W:32") ? Order.OrderStatus.OnTargetPart : Order.OrderStatus.Canceled;
                                    if (!o.SubOrderName.EndsWith("#<noQty/>") && o.SubOrderName.Length < 190)
                                        o.SubOrderName = $"{o.SubOrderName}#<noQty/>";
                                    dc.SaveChanges();
                                    Model.Singleton().CheckForOrderCompletion(r.DTOOrders[i].ERP_ID, r.DTOOrders[i].OrderID);
                                }
                            }
                        }
                        else
                        {
                            for (int i = r.Place.Count; i < r.DTOOrders.Count; i++)
                            {
                                var o = dc.Orders.Find(r.DTOOrders[i].ID);
                                if (o != null)
                                    o.Status = Order.OrderStatus.NotActive;
                                logCounter++;
                                Log.AddLog(Log.SeverityEnum.Event, nameof(DTOOrderToDTOCommand), ($"Warehouse waiting for: {logCounter}: {r.DTOOrders[i].ERP_ID ?? 0}|{r.DTOOrders[i].OrderID}: ({r.Key.SKU_ID:d9}, {r.Key.SKU_Batch}) x {r.Key.SKU_Qty}"));
                            }
                            dc.SaveChanges();
                        }
                    }
                    else
                        for (int i = 0; i < r.Place.Count; i++)
                        {
                            string source = r.Place[i].Place.PlaceID;
                            var cmd = dc.Commands.FirstOrDefault(p => p.Status < Command.CommandStatus.Canceled && 
                                                                      p.Source == source && (p.Target.StartsWith("W:1") || p.Target.StartsWith("W:2")));
                            if (cmd != null)
                                source = cmd.Target;
                            yield return new DTOCommand
                            {
                                TU_ID = r.Place[i].TU.TU_ID,
                                Order_ID = r.DTOOrders[i].ID,
                                Source = source,
                                Target = r.DTOOrders[i].Destination,
                                LastChange = DateTime.Now,
                            };
                        }
                }
            }
        }


        /// <summary>
        /// DTOOrder is connected only to one  DTOCommand 
        /// Order can be connected to many DTOCommands
        /// Order 
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public static IEnumerable<DTOOrder> OrderToDTOOrders(this IEnumerable<Order> orders)
        {
            using (var dc = new WMSContext())
            {
                string destination = "";
                int counter = 0;
                IEnumerable<string> targets = null;
                foreach (Order o in orders)
                {
                    if (o.Destination != destination)
                    {
                        targets =
                            dc.PlaceIds
                            .Where(prop => prop.ID.StartsWith(o.Destination) && prop.DimensionClass != -1)
                            .Select(prop => prop.ID)
                            .ToList();
                        destination = o.Destination;
                        if (dc.Parameters.Find($"Counter[{o.Destination}]") == null)
                        {
                            dc.Parameters.Add(new Parameter { Name = $"Counter[{o.Destination}]", Value = Convert.ToString(0) });
                            dc.SaveChanges();
                        }
                        counter = Convert.ToInt16(dc.Parameters.Find($"Counter[{o.Destination}]").Value);
                    }
                    _counterLast = counter;
                    _destinationLast = destination;

                    double defQty = dc.SKU_IDs.Find(o.SKU_ID).DefaultQty;
                    int fullTUs = (int)(o.SKU_Qty / defQty);
                    double partialQty = o.SKU_Qty - fullTUs * defQty;
                    for (int i = 0; i < fullTUs; i++)
                    {
                        DTOOrder dtoOrder = new DTOOrder(o);
                        dtoOrder.Destination = targets.ElementAt(counter % targets.Count());
                        // alternatively 
                        // dtoOrder.Destination = targets.ElementAt((counter / 12) % targets.Count());
                        dtoOrder.SKU_Qty = defQty;
                        counter++;
                        yield return dtoOrder;
                    }
                    if (partialQty > 0)
                    {
                        DTOOrder dtoOrder = new DTOOrder(o);
                        dtoOrder.Destination = targets.ElementAt(counter % targets.Count());
                        dtoOrder.SKU_Qty = partialQty;
                        counter++;
                        yield return dtoOrder;
                    }
                    o.Status = Order.OrderStatus.Active;
                    dc.Parameters.Find($"Counter[{o.Destination}]").Value = Convert.ToString(counter);
                }
                dc.SaveChanges();
            }
        }


        public static Command ToCommand(this DTOCommand cmd)
        {
            return new Command
            {
                Order_ID = cmd.Order_ID,
                Source = cmd.Source,
                TU_ID = cmd.TU_ID,
                Status = cmd.Status,
                Target = cmd.Target,
                LastChange = cmd.LastChange
            };
        }

        public static MFCS_Proxy.DTOCommand ToProxyDTOCommand(this Command cmd)
        {
            return new MFCS_Proxy.DTOCommand
            {
                Order_ID = cmd.ID,
                Source = cmd.Source,
                Status = (int)cmd.Status,
                Target = cmd.Target,
                Time = cmd.Time,
                TU_ID = cmd.TU_ID
            };
        }

        public static IEnumerable<MFCS_Proxy.DTOCommand> ToProxyDTOCommand(this IEnumerable<Command> cmds)
        {
            return cmds.Select(p => new MFCS_Proxy.DTOCommand
            {
                Order_ID = p.ID,
                Source = p.Source,
                Status = (int)p.Status,
                Target = p.Target,
                Time = p.Time,
                TU_ID = p.TU_ID
            });
        }



        public static IEnumerable<DTOCommand> TakeNeighbour(this IEnumerable<DTOCommand> commands)
        {
            using (var dc = new WMSContext())
            {
                var commandsList = commands.ToList();
                List<string> sourceList = commandsList
                                         .Select(prop => prop.Source.Substring(0, 10) + ":1")
                                         .ToList();

                var Places =
                    (from place in dc.Places
                     where sourceList.Any(prop => prop == place.PlaceID)
                     select place).ToList();

                for (int i = 0; i < commandsList.Count(); i++)
                    yield return new DTOCommand
                    {
                        Order_ID = commandsList[i].Order_ID,
                        Source = commandsList[i].Source.Substring(0, 10) + ":1",
                        TU_ID = Places[i].TU_ID,
                        Target = null,
                        LastChange = DateTime.Now
                    };
            }
        }



        /// <summary>
        /// Find identical TU (skuid,batch,qty) with nearest prod date on level 2 with free level 1
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public static string FindBrotherOnDepth2(this DTOCommand cmd)
        {
            List<string> reck = new List<string> { "W:11", "W:12", "W:21", "W:22" };
            using (var dc = new WMSContext())
            {
                TU tu = dc.TUs.FirstOrDefault(prop => prop.TU_ID == cmd.TU_ID);
                string brother = dc.Places
                    .Where(prop => reck.Any(p => prop.PlaceID.StartsWith(p)) && prop.PlaceID.EndsWith("2"))
                    .Where(prop => prop.FK_PlaceID.Status == 0)
                    .Where(prop => !dc.Places.Any(p => p.PlaceID == prop.PlaceID.Substring(0, 10) + ":1"))
                    .Where(prop => !dc.Commands.Any(p => (p.Source == prop.PlaceID && p.Status < Command.CommandStatus.Canceled) ||
                                                         (p.Target == prop.PlaceID.Substring(0, 10) + ":1" && p.Status < Command.CommandStatus.Canceled)))
                    .Select(prop => new
                    {
                        Place = prop.PlaceID,
                        TU = prop.FK_TU_ID.FK_TU.FirstOrDefault()
                    })
                    .Where(prop => prop.TU.Batch == tu.Batch && prop.TU.SKU_ID == tu.SKU_ID && prop.TU.Qty == tu.Qty)
                    .Union(
                        dc.Commands
                        .Where(prop => reck.Any(p => prop.Target.StartsWith(p)) && prop.Target.EndsWith("2") && prop.Status < Command.CommandStatus.Canceled)
                        .Where(prop => !dc.Commands.Any(p => p.Target == prop.Target.Substring(0, 10) + ":1" && p.Status < Command.CommandStatus.Canceled))
                        .Where(prop => !dc.Places.Any(p => p.PlaceID == prop.Target.Substring(0, 10) + ":1"))
                        .Where(prop => dc.PlaceIds.Any(p => p.ID == prop.Target.Substring(0, 10) + ":1" && p.Status == 0))
                        .Select(prop => new
                        {
                            Place = prop.Target,
                            TU = prop.FK_TU_ID.FK_TU.FirstOrDefault()
                        })
                        .Where(prop => prop.TU.Batch == tu.Batch && prop.TU.SKU_ID == tu.SKU_ID && prop.TU.Qty == tu.Qty)
                    )
                    .Where(prop => prop.Place.EndsWith("2"))
                    .OrderBy(prop => DbFunctions.DiffHours(prop.TU.ExpDate, tu.ExpDate))
                    // add order by production date
                    .Select(prop => prop.Place)
                    .FirstOrDefault();
                return brother;
            }
        }


        /// <summary>
        /// Command move inside warehouse to brother or free place (nearest or random)
        /// </summary>
        /// <param name="commands"></param>
        /// <returns></returns>
        public static IEnumerable<DTOCommand> MoveToBrotherOrFree(this IEnumerable<DTOCommand> commands)
        {
            using (var dc = new WMSContext())
            {
                // Lambda outer join is very strange. 
                // therefore normal structure is used

                List<string> forbidden = new List<string>();

                var brothers =
                    (
                    from cmd in commands.ToList()
                    join tu in dc.TUs on cmd.TU_ID equals tu.TU_ID
                    select new { TU = tu, Command = cmd } into join1
                    group join1 by new { Reck = join1.Command.Source.Substring(0, 3), join1.TU.SKU_ID, join1.TU.Batch, join1.TU.Qty } into gr
                    select new
                    {
                        Key = gr.Key,
                        Count = gr.Count(),
                        Commands = gr.
                                    Select(prop => prop.Command)
                                    .ToList(),
                        Brother =
                                (dc.TUs
                                .Where(p => p.SKU_ID == gr.Key.SKU_ID && p.Batch == gr.Key.Batch && p.Qty == gr.Key.Qty)
                                .Select(p => p.FK_TU_ID.FK_Place.FirstOrDefault())
                                .Where(p => gr.Key.Reck == p.PlaceID.Substring(0, 3) || p.PlaceID.StartsWith("T"))
                                .Union(
                                    dc.Commands
                                    .Where(p1 => p1.Status < Command.CommandStatus.Canceled && p1.Target.StartsWith("W") && (gr.Key.Reck == p1.Target.Substring(0, 3) || gr.Key.Reck.StartsWith("T")))
                                    .Select(p1 => p1.FK_TU_ID.FK_TU.FirstOrDefault())
                                    .Where(p1 => p1.SKU_ID == gr.Key.SKU_ID && p1.Batch == gr.Key.Batch && p1.Qty == gr.Key.Qty)
                                    .Select(p1 => p1.FK_TU_ID.FK_Place.FirstOrDefault())
                                )
                                .Where(p => p.PlaceID.EndsWith("2"))
                                .Where(p => !dc.Places.Any(prop => prop.PlaceID == p.PlaceID.Substring(0, 10) + ":1"))
                                .Where(p => !dc.Commands.Any(prop => prop.Target.Substring(0, 10) == p.PlaceID.Substring(0, 10) && prop.Status < Command.CommandStatus.Canceled))
                                .Take(gr.Count())
                                .Select(p => p.PlaceID.Substring(0, 10) + ":1")
                                ).ToList()
                    })
                    .ToList();


                var lookNearest = new List<DTOCommand>();
                foreach (var br in brothers)
                {
                    lookNearest.AddRange(br.Commands.GetRange(br.Brother.Count(), br.Commands.Count() - br.Brother.Count()));
                    for (int i = 0; i < br.Brother.Count; i++)
                    {
                        forbidden.Add(br.Brother[i]);
                        yield return new DTOCommand
                        {
                            Order_ID = br.Commands[i].Order_ID,
                            Source = br.Commands[i].Source,
                            TU_ID = br.Commands[i].TU_ID,
                            Target = br.Brother[i],
                            LastChange = DateTime.Now
                        };
                    }
                    for (int i = br.Brother.Count; i < br.Commands.Count; i++)
                    {
                        string target = br.Commands[i].GetRandomPlace(forbidden);
                        forbidden.Add(target);
                        yield return new DTOCommand
                        {
                            Order_ID = br.Commands[i].Order_ID,
                            Source = br.Commands[i].Source,
                            TU_ID = br.Commands[i].TU_ID,
                            Target = target,
                            LastChange = DateTime.Now
                        };
                    }
                }

                // empty pallets to be removed
                foreach (var cmd in commands)
                    if (dc.TUs.FirstOrDefault(prop => prop.TU_ID == cmd.TU_ID) == null)
                    {
                        string target = cmd.GetRandomPlace(forbidden);
                        forbidden.Add(target);
                        yield return new DTOCommand
                        {
                            Order_ID = cmd.Order_ID,
                            Source = cmd.Source,
                            TU_ID = cmd.TU_ID,
                            Target = target,
                            LastChange = DateTime.Now
                        };
                    }
            }
        }


        /// <summary>
        /// Get random place 
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        public static string GetRandomPlace(this DTOCommand command, List<string> forbidden)
        {
            using (var dc = new WMSContext())
            {
                int frequencyClass = 0;

                var type = dc.TU_IDs
                            .FirstOrDefault(prop => prop.ID == command.TU_ID);
                var tu = dc.TUs
                         .FirstOrDefault(prop => prop.TU_ID == command.TU_ID);
                if (tu != null)
                {
                    var skuid = dc.SKU_IDs
                                .FirstOrDefault(prop => prop.ID == tu.SKU_ID);
                    frequencyClass = skuid.FrequencyClass;
                }
                var source = dc.PlaceIds
                            .FirstOrDefault(prop => prop.ID == command.Source);

                var bothFree =
                    dc.PlaceIds
                    .Where(p => !p.FK_Places.Any()
                                && p.ID.EndsWith("2")
                                && p.DimensionClass == type.DimensionClass
                                && p.Status == 0
                                && (command.Source.StartsWith("T") ||
                                    (p.ID.Substring(0, 3) == command.Source.Substring(0, 3)))
                                && !p.FK_Target_Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled)
                                && !forbidden.Any(prop => p.ID == prop))
                    .Join(dc.PlaceIds,
                            place => place.ID.Substring(0, 10) + ":1",
                            neighbour => neighbour.ID,
                            (place, neighbour) => new { Place = place, Neighbour = neighbour })
                    .Where(p => !p.Neighbour.FK_Places.Any()
                                && !p.Neighbour.FK_Target_Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled)
                                && p.Neighbour.Status == 0)
                    .Select(p => p.Place);

                Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 1");

                int count = bothFree
                            .Where(p => p.FrequencyClass == frequencyClass)
                            .OrderBy(p => p.ID)
                            .Count();

                Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 1b");

                bothFree = count > 0 ? bothFree
                                        .Where(p => p.FrequencyClass == frequencyClass)
                                        .OrderBy(p => p.ID) :
                                       bothFree
                                       .OrderBy(p => p.ID);
                Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 2");

                if (count == 0)
                    count = bothFree.Count();

                if (count > 0)
                {
                    if (command.Source.StartsWith("W"))
                        bothFree = bothFree
                                    .OrderBy(prop => (prop.PositionHoist - source.PositionHoist) * (prop.PositionHoist - source.PositionHoist) +
                                                     (prop.PositionTravel - source.PositionTravel) * (prop.PositionTravel - source.PositionTravel));

                    Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 3");
                    return command.Source.StartsWith("W") ? bothFree.FirstOrDefault().ID : bothFree.Skip(Random.Next(count)).FirstOrDefault().ID;
                }
                else
                {
                    Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 4");
                    var oneFree =
                        dc.PlaceIds
                        .Where(p => !p.FK_Places.Any()
                                    && p.ID.EndsWith("1")
                                    && p.DimensionClass == type.DimensionClass
                                    && p.Status == 0
                                    && (command.Source.StartsWith("T") ||
                                        (p.ID.Substring(0, 3) == command.Source.Substring(0, 3)))
                                    && !p.FK_Target_Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled)
                                    && !forbidden.Any(prop => p.ID == prop));

                    Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 5");
                    count = oneFree
                            .Where(p => p.FrequencyClass == frequencyClass)
                            .OrderBy(p => p.ID)
                            .Count();

                    oneFree = count > 0 ? oneFree
                                            .Where(p => p.FrequencyClass == frequencyClass)
                                            .OrderBy(p => p.ID) :
                                           oneFree
                                           .OrderBy(p => p.ID);

                    Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 6");
                    if (count == 0)
                        count = oneFree.Count();


                    if (command.Source.StartsWith("W"))
                        oneFree = oneFree
                                    .OrderBy(prop => (prop.PositionHoist - source.PositionHoist) * (prop.PositionHoist - source.PositionHoist) +
                                                        (prop.PositionTravel - source.PositionTravel) * (prop.PositionTravel - source.PositionTravel));

                    count = oneFree.Count();
                    Log.AddLog(Log.SeverityEnum.Event, nameof(GetRandomPlace), $"CreateInputCommand {command.TU_ID}: random 7");
                    if (count != 0)
                        return command.Source.StartsWith("W") ? oneFree.FirstOrDefault().ID : oneFree.Skip(Random.Next(count)).FirstOrDefault().ID;

                    throw new Exception($"Warehouse is full (demand from {command.Source})");
                }
            }
        }
    }
}


