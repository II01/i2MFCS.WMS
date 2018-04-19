﻿using i2MFCS.WMS.Database.DTO;
using i2MFCS.WMS.Database.Tables;
using SimpleLogs;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace i2MFCS.WMS.Core.Business
{

    public class Model
    {
        private static Random Random = new Random();
        private static object _lockOperation = new Random();
        private static object _lockSingleton = new object();
        private static Model _singleton = null;

        private Timer _timer;
        private SimulateERP _simulateERP;
        private string _erpUser;
        private string _erpPwd;
        private byte _erpCode = 0;

        public Model()
        {
            _timer = new Timer(ActionOnTimer, null, 1000, 2000);
            _simulateERP = new SimulateERP();

            _erpUser = ConfigurationManager.AppSettings["erpUser"];
            _erpPwd = ConfigurationManager.AppSettings["erpPwd"];
            byte.TryParse(ConfigurationManager.AppSettings["erpCode"], out _erpCode);
        }

        public void ActionOnTimer(object state)
        {
            lock (this)
            {
                try
                {
                    _simulateERP.SimulateIncomingTUs("T014", "MAT03", "BATCH04", 5);
                    CreateInputCommand();
                    CreateOutputCommands();
                }
                catch (Exception ex)
                {
                    Log.AddException(ex);
                    SimpleLog.AddException(ex, nameof(Model));
                    Debug.WriteLine(ex.Message);
                }
            }
        }

        // strict FIFO 
        public void CreateOutputCommands()
        {
            try
            {
                using (var dc = new WMSContext())
                using (var ts = dc.Database.BeginTransaction())
                {
                    DateTime now = DateTime.Now;
                    var findOrders = dc.Orders
                            .Where(p => p.Status < Order.OrderStatus.Canceled)
                            .GroupBy(
                            (by) => new { by.Destination },
                            (key,group) => new
                            {
                                CurrentOrder = group.FirstOrDefault(p => p.Status > Order.OrderStatus.NotActive && p.Status < Order.OrderStatus.Canceled),
                                CurrentSubOrder = group.FirstOrDefault(p => p.Status > Order.OrderStatus.NotActive && p.Status < Order.OrderStatus.OnTarget),
                                NewOrder = group.FirstOrDefault(p=>p.Status == Order.OrderStatus.NotActive)
                            }
                            )
                            .Where(p => p.NewOrder != null)
                            .Where(p =>
                                    p.CurrentOrder == null || (p.CurrentOrder.ERP_ID == p.NewOrder.ERP_ID && p.CurrentOrder.OrderID == p.NewOrder.OrderID))
                            .Where( p => 
                                    (p.CurrentSubOrder == null || (p.CurrentSubOrder.ERP_ID == p.CurrentOrder.ERP_ID && p.CurrentSubOrder.OrderID == p.CurrentOrder.OrderID && p.NewOrder.SubOrderID == p.CurrentSubOrder.SubOrderID)))
                            .Select( p => p.NewOrder)
                            .Where(p => p.ReleaseTime < now)
                            .ToList();

                    /// Alternative faster solution
                    /// Create DTOOrders from Orders
                    List<DTOOrder> dtoOrders =
                        findOrders
                        .OrderToDTOOrders()
                        .ToList();


                    // create DTO commands
                    List<DTOCommand> cmdList =
                        dtoOrders
                        .DTOOrderToDTOCommand()
                        .ToList();


                    if (cmdList.Count > 0)
                    {
                        var cmdSortedFromOne = cmdList
                                        .OrderByDescending(prop => prop.Source.EndsWith("1"))
                                        .ThenByDescending(prop => prop.Source);


                        List<DTOCommand> transferProblemCmd = cmdList
                                         .Where(prop => prop.Source.EndsWith("2"))
                                         .Where(prop => !cmdList.Any(cmd => cmd.Source == prop.Source.Substring(0, 10) + ":1"))
                                         .Join(dc.Places,
                                                command => command.Source.Substring(0, 10) + ":1",
                                                neighbour => neighbour.PlaceID,
                                                (command, neighbour) => new { Command = command, Neighbour = neighbour }
                                         )
                                         .Where(prop => !prop.Neighbour.FK_PlaceID.FK_Source_Commands.Any())
                                         .Select(prop => prop.Command)
                                         .ToList();

                        List<DTOCommand> transferCmd = transferProblemCmd
                                        .TakeNeighbour()
                                        .MoveToBrotherOrFree()
                                        .ToList();

                        dc.SaveChanges();
                        var commands = new List<Command>();
                        foreach (var cmd in cmdSortedFromOne)
                        {
                            int i = transferProblemCmd.IndexOf(cmd);
                            if (i != -1)
                            {
                                Debug.WriteLine($"Transfer command : {transferCmd[i].ToString()}");
                                SimpleLog.AddLog(SimpleLog.Severity.EVENT, nameof(Model), $"Transfer command : {transferCmd[i].ToString()}", "");
                                Log.AddLog(Log.SeverityEnum.Event, nameof(CreateOutputCommands), $"Transfer command : {transferCmd[i].ToString()}");
                                commands.Add(transferCmd[i].ToCommand());
                            }
                            Debug.WriteLine($"Output command : {cmd.ToString()}");
                            SimpleLog.AddLog(SimpleLog.Severity.EVENT, nameof(Model), $"Output command : {cmd.ToString()}", "");
                            Log.AddLog(Log.SeverityEnum.Event, nameof(CreateOutputCommands), $"Output command : {cmd.ToString()}");
                            commands.Add(cmd.ToCommand());
                        }
                        dc.Commands.AddRange(commands);
                        // notify ERP about changes

                        dc.SaveChanges();
                        using (MFCS_Proxy.WMSClient proxy = new MFCS_Proxy.WMSClient())
                        {
                            proxy.MFCS_Submit(commands.ToProxyDTOCommand().ToArray());
                        }
                        ts.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                SimpleLog.AddException(ex, nameof(Model));
                Log.AddException(ex);
                Debug.WriteLine(ex.Message);
            }
        }

        public void CreateInputCommand()
        {
            try
            {
                using (var dc = new WMSContext())
                using (var ts = dc.Database.BeginTransaction())
                {
                    string source = dc.Parameters.Find("InputCommand.Place").Value;
                    List<string> forbidden = new List<string>();
                    Place place = dc.Places.FirstOrDefault(prop => prop.PlaceID == source);
                    if (place != null)
                    {
                        TU tu = dc.TUs.FirstOrDefault(prop => prop.TU_ID == place.TU_ID);
                        if (tu == null)
                        {
                            // add ERP notitication here
                            var xmlErp = new Core.Xml.XmlWriteMovementToSB
                            {
                                DocumentID = 0,
                                DocumentType = nameof(Xml.XmlWriteMovementToSB),
                                TU_IDs = new int[] { place.TU_ID }
                            };

                            string searchFor = xmlErp.Reference();
                            if (dc.CommandERP.FirstOrDefault(prop => prop.Reference == searchFor) == null)
                            {
                                CommandERP erpCmd = new CommandERP
                                {
                                    ERP_ID = 0,
                                    Command = xmlErp.BuildXml(),
                                    Reference = xmlErp.Reference(),
                                    Status = 0,
                                    LastChange = DateTime.Now
                                };
                                dc.CommandERP.Add(erpCmd);
                                dc.SaveChanges();
                                xmlErp.DocumentID = erpCmd.ID;
                                erpCmd.Command = xmlErp.BuildXml();
                                dc.SaveChanges();
                                // make call to ERP via WCF
                                using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                                {
                                    // var retVal = proxyERP.WriteMovementToSBWithBarcode("a", "b", 123, erpCmd.Command, "e");
                                    //retVal[0].ResultType;
                                    //retVal[0].ResultString;
                                }
                                erpCmd.Status = 3;
                                dc.SaveChanges();
                                ts.Commit();
                                Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CommandERP created : {erpCmd.Reference}");
                            }
                        }
                        else if (place != null && !place.FK_PlaceID.FK_Source_Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled && prop.TU_ID == place.TU_ID)
                            && tu != null)
                        {
                            var cmd = new DTOCommand
                            {
                                Order_ID = null,
                                TU_ID = place.TU_ID,
                                Source = source,
                                Target = null,
                                LastChange = DateTime.Now,
                                Status = 0                               
                            };
                            string brother = cmd.FindBrotherOnDepth2();
                            if (brother != null)
                                cmd.Target = brother.Substring(0, 10) + ":1";
                            else
                                cmd.Target = cmd.GetRandomPlace(forbidden);
                            Command c = cmd.ToCommand();
                            dc.Commands.Add(c);
                            dc.SaveChanges();
                            using (MFCS_Proxy.WMSClient proxy = new MFCS_Proxy.WMSClient())
                            {
                                MFCS_Proxy.DTOCommand[] cs = new MFCS_Proxy.DTOCommand[] { c.ToProxyDTOCommand() };
                                proxy.MFCS_Submit(cs);
                            }
                            ts.Commit();
                            Debug.WriteLine($"Input command for {source} crated : {cmd.ToString()}");
                            SimpleLog.AddLog(SimpleLog.Severity.EVENT, nameof(Model), $"Command created : {c.ToString()}", "");
                            Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"Command created : {c.ToString()}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
            }
        }



        public void CreateDatabase()
        {
            try
            {
                using (WMSContext dc = new WMSContext())
                {
                    dc.Database.Delete();
                    dc.Database.Create();
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public static Model Singleton()
        {
            if (_singleton == null)
                lock (_lockSingleton)
                    if (_singleton == null)
                        _singleton = new Model();
            return _singleton;
        }


        public bool CommandChangeNotifyERP(Command command)
        {
            try
            {
                using (var dc = new WMSContext())
                using (var ts = dc.Database.BeginTransaction())
                {

                    if (command.Order_ID != null)
                    {
                        // check if single item finished
                        Order order = dc.Orders.FirstOrDefault(prop => prop.ID == command.Order_ID);
                        bool oItemFinished = !dc.Commands
                                        .Where(prop => prop.Status < Command.CommandStatus.Finished && prop.Order_ID == order.ID)
                                        .Any();
                        bool oItemCanceled = dc.Commands.Any(prop => prop.Status == Command.CommandStatus.Canceled && prop.Order_ID == order.ID)
                                             && !dc.Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled && prop.Order_ID == order.ID);

                        bool boolOrdersFinished = !dc.Orders.Any(prop => prop.OrderID == order.OrderID && prop.ERP_ID == order.ERP_ID && prop.Status < Order.OrderStatus.OnTarget);

                        // check if subOrderFinished for one SKU
                        if (oItemFinished || oItemCanceled)
                        {
                            order.Status = oItemFinished ? Order.OrderStatus.OnTarget : Order.OrderStatus.WaitForTakeoff;
                            if (order.ERP_ID.HasValue && boolOrdersFinished)
                            {
                                Xml.XmlReadERPCommandStatus xmlStatus = new Xml.XmlReadERPCommandStatus
                                {
                                    OrderToReport = dc.Orders.Where(prop => prop.OrderID == order.OrderID && prop.ERP_ID == order.ERP_ID)
                                };
                                CommandERP cmdERP1 = new CommandERP
                                {
                                    ERP_ID = order.ERP_ID.Value,
                                    Command = xmlStatus.BuildXml(),
                                    Reference = xmlStatus.Reference(),
                                    LastChange = DateTime.Now,
                                    Status = 3
                                };
                                dc.CommandERP.Add(cmdERP1);
                                Log.AddLog(Log.SeverityEnum.Event, nameof(CommandChangeNotifyERP), $"CommandERP created : {cmdERP1.Reference}");
                            }
                            dc.SaveChanges();
                            // TODO-WMS call XmlReadERPCommandStatus via WCF
                        }

                        Xml.XmlWritePickToDocument xmlPickDocument = new Xml.XmlWritePickToDocument
                        {
                            DocumentID = order.ERP_ID.HasValue ? order.ERP_ID.Value : 0,
                            Commands = new Command[] { command }
                        };
                        CommandERP cmdERP;
                        dc.CommandERP.Add(cmdERP = new CommandERP
                        {
                            ERP_ID = order.ERP_ID.HasValue ? order.ERP_ID.Value : 0,
                            Command = xmlPickDocument.BuildXml(),
                            Reference = xmlPickDocument.Reference(),
                            LastChange = DateTime.Now
                        });
                        Log.AddLog(Log.SeverityEnum.Event, nameof(CommandChangeNotifyERP), $"CommandERP created : {cmdERP.Reference}");
                        dc.SaveChanges();
                        // TODO-WMS call XMlWritePickToDocument
                        using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                        {
                            //var retVal = proxyERP.WritePickToDocument("a", "b", 0, "d", "e");
                            //retVal[0].ResultType;
                            //retVal[0].ResultString;
                        }
                        cmdERP.Status = 3;
                        dc.SaveChanges();
                    }
                    ts.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
                throw;
            }
        }


        public void MFCSUpdatePlace(string placeID, int TU_ID, string changeType)
        {
            try
            {
                //                lock (this)
                {
                    using (var dc = new WMSContext())
                    using (var ts = dc.Database.BeginTransaction())
                    {
                        string entry = dc.Parameters.Find("InputCommand.Place").Value;

                        Place p = dc.Places
                                    .Where(prop => prop.TU_ID == TU_ID)
                                    .FirstOrDefault();
                        TU_ID tuid = dc.TU_IDs.Find(TU_ID);
                        if (tuid == null)
                        {
                            dc.TU_IDs.Add(new TU_ID
                            {
                                ID = TU_ID
                            });
                            Log.AddLog(Log.SeverityEnum.Event, nameof(MFCSUpdatePlace), $"TU_IDs add : {TU_ID:d9}");
                        }
                        if (p == null || p.PlaceID != placeID)
                        {
                            if (p != null)
                                dc.Places.Remove(p);
                            dc.Places.Add(new Place
                            {
                                PlaceID = placeID,
                                TU_ID = TU_ID
                            });
                            Log.AddLog(Log.SeverityEnum.Event, nameof(MFCSUpdatePlace), $"{placeID},{TU_ID:d9}");
                        }
                        dc.SaveChanges();
                        ts.Commit();

                        if (placeID == entry && (changeType.StartsWith("MOVE") || changeType.StartsWith("INFO"))) // notify ERP on pallet entry
                        {
                            // first we create a command to get an ID
                            CommandERP cmd = new CommandERP
                            {
                                ERP_ID = 0,
                                Command = "Entry",
                                Reference = "Entry",
                                Status = 0,
                                Time = DateTime.Now,
                                LastChange = DateTime.Now
                            };
                            dc.CommandERP.Add(cmd);
                            dc.SaveChanges();
                            // create xml
                            string docType = "ATR01";
                            if (changeType.Contains("ERR"))
                                docType = "ATR03";
                            Xml.XmlWriteMovementToSB xmlWriteMovement = new Xml.XmlWriteMovementToSB
                            {
                                DocumentID = cmd.ID,
                                DocumentType = docType,
                                TU_IDs = new int[] { TU_ID }                                    
                            };
                            string reply = "";
                            using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                            {
                                try
                                {
                                    var retVal = proxyERP.WriteMovementToSBWithBarcode(_erpUser, _erpPwd, _erpCode, xmlWriteMovement.BuildXml(), "");
                                    reply = $"<reply>\n\t<type>{retVal[0].ResultType}</type>\n\t<string>{retVal[0].ResultString}</string>\n</reply>";
                                }
                                catch (Exception ex)
                                {
                                    reply = $"<\reply>\n\t<type>{1}</type>\n\t<string>{ex.Message}</string><\reply>";
                                }
                            }
                            // write to ERPcommands
                            cmd.Command = $"{xmlWriteMovement.BuildXml()}\n\n<!-- Reply -->\n\n{reply}";
                            cmd.Reference = xmlWriteMovement.Reference();
                            cmd.LastChange = DateTime.Now;
                            cmd.Status = 3; // finished
                            dc.SaveChanges();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
                throw;
            }
        }


        public void MFCSUpdateCommand(int id, int status)
        {
            try
            {
                //                lock (this)
                {
                    using (var dc = new WMSContext())
                    {
                        var cmd = dc.Commands.Find(id);
//                        Command.CommandStatus oldS = cmd.Status;
                        cmd.Status = (Command.CommandStatus)status;
                        cmd.LastChange = DateTime.Now;
                        dc.SaveChanges();
                        Log.AddLog(Log.SeverityEnum.Event, nameof(MFCSUpdateCommand), $"{id}, {status}");
                        // if (oldS != cmd.Status && cmd.Status >= Command.CommandStatus.Finished)
                        CommandChangeNotifyERP(cmd);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
                throw;
            }
        }

        public void CancelOrderCommands(DTOOrder orderToCancel)
        {
            try
            {
                using (var dc = new WMSContext())
                using (var client = new MFCS_Proxy.WMSClient())
                {
                    var order = dc.Orders.FirstOrDefault(p => p.ERP_ID == orderToCancel.ERP_ID && p.OrderID == orderToCancel.OrderID);
                    if (order != null)
                    {
                        var cmds = dc.Commands.Where(p => p.Order_ID == order.ID && p.Status <= Command.CommandStatus.Active);
                        foreach (var c in cmds)
                        {
                            c.Status = Command.CommandStatus.Canceled;
                            MFCS_Proxy.DTOCommand[] cs = new MFCS_Proxy.DTOCommand[] { c.ToProxyDTOCommand() };
                            client.MFCS_Submit(cs);
                        }
                    }
                    Log.AddLog(Log.SeverityEnum.Event, nameof(CancelOrderCommands), $"Order canceled: {orderToCancel.ToString()}");
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
            }
        }
        public void CancelCommand(DTOCommand cmdToCancel)
        {
            try
            {
                using (var dc = new WMSContext())
                using (var client = new MFCS_Proxy.WMSClient())
                {
                    var cmd = dc.Commands.Find(cmdToCancel.ID);
                    if (cmd != null)
                    {
                        cmd.Status = Command.CommandStatus.Canceled;
                        MFCS_Proxy.DTOCommand[] cs = new MFCS_Proxy.DTOCommand[] { cmd.ToProxyDTOCommand() };
                        client.MFCS_Submit(cs);
                    }
                    Log.AddLog(Log.SeverityEnum.Event, nameof(CancelCommand), $"Command canceled: {cmdToCancel.ToString()}");
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
            }
        }
        public void BlockLocations(string locStartsWith, bool block, int reason)
        {
            try
            {
                using (var dc = new WMSContext())
                {
                    dc.Database.CommandTimeout = 180;

                    var items = (from pid in dc.PlaceIds
                                 where pid.DimensionClass >= 0 && pid.DimensionClass <= 999 &&
                                       pid.ID.StartsWith(locStartsWith) && ((pid.Status & reason) > 0) != block
                                 join p in dc.Places on pid.ID equals p.PlaceID into grp
                                 from g in grp.DefaultIfEmpty()
                                 select new { PID = pid, TU = g }).ToList();


                    List<int> tuids = new List<int>();

                    if (items != null && items.Count > 0)
                    {
                        // update database
                        if (block)
                        {

                            foreach (var i in items)
                            {
                                i.PID.Status = i.PID.Status | reason;
                                if (i.TU != null)
                                {
                                    i.TU.FK_TU_ID.Blocked = i.TU.FK_TU_ID.Blocked | reason;
                                    tuids.Add(i.TU.TU_ID);
                                }

                            }
                        }
                        else
                        {
                            int mask = int.MaxValue ^ reason;
                            foreach (var i in items)
                            {
                                i.PID.Status = i.PID.Status & mask;
                                if (i.TU != null)
                                {
                                    i.TU.FK_TU_ID.Blocked = i.TU.FK_TU_ID.Blocked & mask;
                                    tuids.Add(i.TU.TU_ID);
                                }
                            }
                        }

                        // inform MFCS
                        using (var client = new MFCS_Proxy.WMSClient())
                        {
                            string[] la = new string[] { locStartsWith };
                            if (block)
                                client.MFCS_PlaceBlock(la, 0);
                            else
                                client.MFCS_PlaceUnblock(la, 0);
                        }
                        string blocked = block ? "blocked" : "released";

                        Log.AddLog(Log.SeverityEnum.Event, nameof(BlockLocations), $"Locations {blocked}: {locStartsWith}* ({reason})");
                        dc.SaveChanges();

                        // inform ERP
                        // first we create a command to get an ID
                        if (tuids.Count > 0)
                        {
                            CommandERP cmd = new CommandERP
                            {
                                ERP_ID = 0,
                                Command = "BlockPlace",
                                Reference = "BlockPlace",
                                Status = 0,
                                Time = DateTime.Now,
                                LastChange = DateTime.Now
                            };
                            dc.CommandERP.Add(cmd);
                            dc.SaveChanges();
                            // create xml
                            string docType = block ? "ATS01" : "ATS02";
                            Xml.XmlWriteMovementToSB xmlWriteMovement = new Xml.XmlWriteMovementToSB
                            {
                                DocumentID = cmd.ID,
                                DocumentType = docType,
                                TU_IDs = tuids.ToArray()
                            };
                            string reply = "";
                            using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                            {
                                try
                                {
                                    var retVal = proxyERP.WriteMovementToSBWithBarcode(_erpUser, _erpPwd, _erpCode, xmlWriteMovement.BuildXml(), "");
                                    reply = $"<reply>\n\t<type>{retVal[0].ResultType}</type>\n\t<string>{retVal[0].ResultString}</string>\n</reply>";
                                }
                                catch (Exception ex)
                                {
                                    reply = $"<\reply>\n\t<type>{1}</type>\n\t<string>{ex.Message}</string><\reply>";
                                }
                            }
                            // write to ERPcommands
                            cmd.Command = $"{xmlWriteMovement.BuildXml()}\n\n<!-- Reply -->\n\n{reply}";
                            cmd.Reference = xmlWriteMovement.Reference();
                            cmd.LastChange = DateTime.Now;
                            cmd.Status = 3; // finished
                            dc.SaveChanges();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
            }
        }
        public void BlockTU(int TUID, bool block, int reason)
        {
            try
            {
                List<int> tuids = new List<int>();

                using (var dc = new WMSContext())
                {
                    var items = (from tuid in dc.TU_IDs
                                 where tuid.ID == TUID
                                 select tuid).ToList();
                    if (block)
                    {
                        foreach (var i in items)
                        {
                            i.Blocked = i.Blocked | reason;
                            tuids.Add(i.ID);
                        }
                    }
                    else
                    {
                        int mask = int.MaxValue ^ reason;
                        foreach (var i in items)
                        {
                            i.Blocked = i.Blocked & mask;
                            tuids.Add(i.ID);
                        }
                    }
                    string blocked = block ? "blocked" : "released";
                    Log.AddLog(Log.SeverityEnum.Event, nameof(BlockTU), $"TU {blocked}: {TUID}* ({reason})");
                    dc.SaveChanges();

                    // inform ERP
                    if (items.Count > 0)
                    {
                        // first we create a command to get an ID
                        CommandERP cmd = new CommandERP
                        {
                            ERP_ID = 0,
                            Command = "BlockTU",
                            Reference = "BlockTU",
                            Status = 0,
                            Time = DateTime.Now,
                            LastChange = DateTime.Now
                        };
                        dc.CommandERP.Add(cmd);
                        dc.SaveChanges();
                        // create xml
                        string docType = block ? "ATS01" : "ATS02";
                        Xml.XmlWriteMovementToSB xmlWriteMovement = new Xml.XmlWriteMovementToSB
                        {
                            DocumentID = cmd.ID,
                            DocumentType = docType,
                            TU_IDs = tuids.ToArray()
                        };
                        string reply = "";
                        using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                        {
                            try
                            {
                                var retVal = proxyERP.WriteMovementToSBWithBarcode("WEBSERVICE", "webservice", 1, xmlWriteMovement.BuildXml(), "");
                                reply = $"<reply>\n\t<type>{retVal[0].ResultType}</type>\n\t<string>{retVal[0].ResultString}</string>\n</reply>";
                            }
                            catch (Exception ex)
                            {
                                reply = $"<\reply>\n\t<type>{1}</type>\n\t<string>{ex.Message}</string><\reply>";
                            }
                        }
                        // write to ERPcommands
                        cmd.Command = $"{xmlWriteMovement.BuildXml()}\n\n<!-- Reply -->\n\n{reply}";
                        cmd.Reference = xmlWriteMovement.Reference();
                        cmd.LastChange = DateTime.Now;
                        cmd.Status = 3; // finished
                        dc.SaveChanges();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
            }
        }

        public void ReleaseRamp(string destinationtStartsWith)
        {
            try
            {
                using (var dc = new WMSContext())
                {

                    bool canRelease = !dc.Places.Any(p => p.PlaceID.StartsWith(destinationtStartsWith));

                    if (canRelease)
                    {
                        var l = from o in dc.Orders
                                where o.Destination.StartsWith(destinationtStartsWith) &&
                                      o.Status == Order.OrderStatus.OnTarget || o.Status == Order.OrderStatus.WaitForTakeoff
                                select o;
                        foreach (var o in l)
                            o.Status = (o.Status == Order.OrderStatus.OnTarget) ? Order.OrderStatus.Finished : Order.OrderStatus.Canceled;
                        Log.AddLog(Log.SeverityEnum.Event, nameof(BlockTU), $"Ramp released: {destinationtStartsWith}");
                        dc.SaveChanges();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
            }
        }


    }
}