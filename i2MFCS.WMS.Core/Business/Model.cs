using i2MFCS.WMS.Core.DataExchange;
using i2MFCS.WMS.Database.DTO;
using i2MFCS.WMS.Database.Tables;
using SimpleLogs;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace i2MFCS.WMS.Core.Business
{

    public class Model
    {
        private static Random Random = new Random();
        private static object _lockSingleton = new object();
        private static object _lockTimer = new object();
        private static Model _singleton = null;

        private Timer _timer;
        private SimulateERP _simulateERP;
        private string _erpUser;
        private string _erpPwd;
        private byte _erpCode = 0;
        private Random _rnd = new Random();

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
            lock (_lockTimer)
            {
                try
                {
//                    _simulateERP.SimulateIncomingTUs("T014", $"MAT0{_rnd.Next(1, 3)}", $"BATCH0{_rnd.Next(1, 3)}", 5);
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
                    try
                    {
                        DateTime now = DateTime.Now;
                        var findOrders = dc.Orders
                                .Where(p => p.Status < Order.OrderStatus.Canceled)
                                .GroupBy(
                                (by) => new { by.Destination },
                                (key, group) => new
                                {
                                    CurrentOrder = group.FirstOrDefault(p => p.Status > Order.OrderStatus.NotActive && p.Status < Order.OrderStatus.Canceled),
                                    CurrentSubOrder = group.FirstOrDefault(p => p.Status > Order.OrderStatus.NotActive && p.Status < Order.OrderStatus.OnTargetPart),
                                    NewOrder = group.FirstOrDefault(p => p.Status == Order.OrderStatus.NotActive)
                                }
                                )
                                .Where(p => p.NewOrder != null)
                                .Where(p =>
                                        p.CurrentOrder == null || (p.CurrentOrder.ERP_ID == p.NewOrder.ERP_ID && p.CurrentOrder.OrderID == p.NewOrder.OrderID))
                                .Where(p =>
                                       (p.CurrentSubOrder == null || (p.CurrentSubOrder.ERP_ID == p.CurrentOrder.ERP_ID && p.CurrentSubOrder.OrderID == p.CurrentOrder.OrderID && p.NewOrder.SubOrderID == p.CurrentSubOrder.SubOrderID)))
                                .Select(p => p.NewOrder)
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
                                            .OrderByDescending(prop => prop.Source.Substring(0, 10))
                                            .ThenBy(prop => prop.Source.Last());

                            List<DTOCommand> transferProblemCmd = cmdList
                                             .Where(prop => prop.Source.EndsWith("2"))
                                             .Where(prop => !cmdList.Any(cmd => cmd.Source == prop.Source.Substring(0, 10) + ":1"))
                                             .Join(dc.Places,
                                                    command => command.Source.Substring(0, 10) + ":1",
                                                    neighbour => neighbour.PlaceID,
                                                    (command, neighbour) => new { Command = command, Neighbour = neighbour }
                                             )
                                             .Where(prop => !prop.Neighbour.FK_PlaceID.FK_Source_Commands.Any(p => p.Status < Command.CommandStatus.Canceled))
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
                    catch (Exception)
                    {
                        ts.Rollback();
                        throw;
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
                    try
                    {
                        string source = dc.Parameters.Find("InputCommand.Place").Value;
                        List<string> forbidden = new List<string>();
                        Place place = dc.Places.FirstOrDefault(prop => prop.PlaceID == source);
                        if (place != null)
                        {
                            Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CreateInputCommand {place.TU_ID}: start");

                            int noTUs = dc.TUs.Count(prop => prop.TU_ID == place.TU_ID);
                            if (place != null && noTUs == 1 &&
                                !place.FK_PlaceID.FK_Source_Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled && prop.TU_ID == place.TU_ID))
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
                                Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CreateInputCommand {place.TU_ID}: brother start");
                                string brother = cmd.FindBrotherOnDepth2();
                                Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CreateInputCommand {place.TU_ID}: brother end");
                                if (brother != null)
                                    cmd.Target = brother.Substring(0, 10) + ":1";
                                else
                                {
                                    Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CreateInputCommand {place.TU_ID}: random start");
                                    cmd.Target = cmd.GetRandomPlace(forbidden);
                                    Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CreateInputCommand {place.TU_ID}: random end");
                                }
                                Command c = cmd.ToCommand();
                                dc.Commands.Add(c);
                                dc.SaveChanges();
                                using (MFCS_Proxy.WMSClient proxy = new MFCS_Proxy.WMSClient())
                                {
                                    MFCS_Proxy.DTOCommand[] cs = new MFCS_Proxy.DTOCommand[] { c.ToProxyDTOCommand() };
                                    proxy.MFCS_Submit(cs);
                                }
                                ts.Commit();

                                string selectionType = brother != null ? "Brother" : "Random";
                                Debug.WriteLine($"Input command for {source} crated : {cmd.ToString()}, {selectionType}");
                                SimpleLog.AddLog(SimpleLog.Severity.EVENT, nameof(Model), $"Command created : {c.ToString()}, {selectionType}", "");
                                Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"Command created : {c.ToString()}, {selectionType}");
                            }

                            Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"CreateInputCommand {place.TU_ID}: done");
                        }
                    }
                    catch (Exception)
                    {
                        ts.Rollback();
                        throw;
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


        protected async Task ERP_WritePickToDocument(CommandERP cmdERP, bool ignoreException)
        {
            // Notify ERP
            bool.TryParse(ConfigurationManager.AppSettings["ERPpresent"], out bool erpPresent);

            using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
            {
                string reply = "";
                try
                {
                    if (erpPresent)
                    {
                        var retVal = await proxyERP.WritePickToDocumentAsync(_erpUser, _erpPwd, _erpCode, cmdERP.Command, "");
                        reply = $"<reply>\n\t<type>{retVal[0].ResultType}</type>\n\t<string>{retVal[0].ResultString}</string>\n</reply>";

                        cmdERP.Status = (retVal[0].ResultType == ERP_Proxy.clsERBelgeSonucTip.OK || ignoreException) ?
                                        CommandERP.CommandERPStatus.Finished : CommandERP.CommandERPStatus.Error;
                    }
                }
                catch (Exception ex)
                {
                    reply = $"<reply>\n\t<type>{1}</type>\n\t<string>{ex.Message}</string></reply>";
                    Log.AddException(ex);
                    SimpleLog.AddException(ex, nameof(Model));
                    Debug.WriteLine(ex.Message);
                    cmdERP.Status = CommandERP.CommandERPStatus.Error;
                }
                cmdERP.Command = $"<!-- CALL -->\n{cmdERP.Command}\n\n<!-- REPLY\n{reply}\n-->";

                using (var dc = new WMSContext())
                {
                    var cmd = dc.CommandERP.Find(cmdERP.ID);
                    cmd.Command = cmdERP.Command;
                    cmd.Status = cmdERP.Status;
                    dc.SaveChanges();
                }
            }
        }

        protected async Task ERP_WriteResultToSB(CommandERP cmdERP)
        {
            try
            {
                bool.TryParse(ConfigurationManager.AppSettings["ERPpresent"], out bool erpPresent);

                using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                {
                    string reply = "";
                    try
                    {
                        if (erpPresent)
                        {
                            var retVal = await proxyERP.WriteResultToSBAsync(_erpUser, _erpPwd, _erpCode, cmdERP.Command, "");
                            reply = $"<reply>\n\t<type>{retVal[0].ResultType}</type>\n\t<string>{retVal[0].ResultString}</string>\n</reply>";
                            cmdERP.Status = retVal[0].ResultType == ERP_Proxy.clsERBelgeSonucTip.OK ? CommandERP.CommandERPStatus.Finished : CommandERP.CommandERPStatus.Error;
                        }
                    }
                    catch (Exception ex)
                    {
                        reply = $"<reply>\n\t<type>{1}</type>\n\t<string>{ex.Message}</string></reply>";
                        cmdERP.Status = CommandERP.CommandERPStatus.Error;
                        Log.AddException(ex);
                        SimpleLog.AddException(ex, nameof(Model));
                        Debug.WriteLine(ex.Message);
                    }
                    // write to ERPcommands
                    cmdERP.Command = $"<!-- CALL -->\n{cmdERP.Command}\n\n<!-- REPLY\n{reply}\n-->";
                    using (var dc = new WMSContext())
                    {
                        var cmd = dc.CommandERP.Find(cmdERP.ID);
                        cmd.Command = cmdERP.Command;
                        cmd.Reference = cmdERP.Reference;
                        cmd.Status = cmdERP.Status;
                        dc.SaveChanges();
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


        protected async Task ERP_WriteMovementToSB(CommandERP cmdERP, string place, int? tuid, bool commandGeneration)
        {
            try
            {
                string reply = "";
                bool sendToOut = false;

                bool.TryParse(ConfigurationManager.AppSettings["ERPpresent"], out bool erpPresent);

                using (ERP_Proxy.SBWSSoapClient proxyERP = new ERP_Proxy.SBWSSoapClient())
                {
                    try
                    {
                        if (erpPresent)
                        {
                            var retVal = await proxyERP.WriteMovementToSBWithBarcodeAsync(_erpUser, _erpPwd, _erpCode, cmdERP.Command, "");
                            reply = $"<reply>\n\t<type>{retVal[0].ResultType}</type>\n\t<string>{retVal[0].ResultString}</string>\n</reply>";
                            cmdERP.Status = retVal[0].ResultType == ERP_Proxy.clsERBelgeSonucTip.OK ? CommandERP.CommandERPStatus.Finished : CommandERP.CommandERPStatus.Error;
                            if (retVal[0].ResultType != ERP_Proxy.clsERBelgeSonucTip.OK)
                                sendToOut = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        reply = $"<reply>\n\t<type>{1}</type>\n\t<string>{ex.Message}</string></reply>";
                        cmdERP.Status = CommandERP.CommandERPStatus.Error;
                        Log.AddException(ex);
                        SimpleLog.AddException(ex, nameof(Model));
                        Debug.WriteLine(ex.Message);
                        sendToOut = true;
                    }
                    if (sendToOut && tuid != null && commandGeneration)
                    {
                        using (var dc = new WMSContext())
                        using (var ts = dc.Database.BeginTransaction())
                        {
                            try
                            {
                                string entry = dc.Parameters.Find("InputCommand.Place").Value;
                                string output = dc.Parameters.Find("DefaultOutput.Place").Value;
                                if (place == entry)
                                {
                                    var cmd = new DTOCommand
                                    {
                                        Order_ID = null,
                                        TU_ID = tuid.Value,
                                        Source = entry,
                                        Target = output,
                                        LastChange = DateTime.Now,
                                        Status = 0
                                    };
                                    Command c = cmd.ToCommand();
                                    dc.Commands.Add(c);
                                    dc.SaveChanges();
                                    using (MFCS_Proxy.WMSClient proxy = new MFCS_Proxy.WMSClient())
                                    {
                                        MFCS_Proxy.DTOCommand[] cs = new MFCS_Proxy.DTOCommand[] { c.ToProxyDTOCommand() };
                                        proxy.MFCS_Submit(cs);
                                    }
                                    ts.Commit();
                                    Debug.WriteLine($"Input command for {entry} crated : {cmd.ToString()}");
                                    SimpleLog.AddLog(SimpleLog.Severity.EVENT, nameof(Model), $"Command created : {c.ToString()}", "");
                                    Log.AddLog(Log.SeverityEnum.Event, nameof(CreateInputCommand), $"Command created : {c.ToString()}");
                                }
                            }
                            catch (Exception)
                            {
                                ts.Rollback();
                                throw;
                            }
                        }
                    }
                }
                // write to ERPcommands
                cmdERP.Command = $"<!-- CALL -->\n{cmdERP.Command}\n\n<!-- REPLY\n{reply}\n-->";

                using (var dc = new WMSContext())
                {
                    var cmd = dc.CommandERP.Find(cmdERP.ID);
                    cmd.Command = cmdERP.Command;
                    cmd.Reference = cmdERP.Reference;
                    cmd.Status = cmdERP.Status;
                    dc.SaveChanges();
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

        public void MoveOrderToHist(int? erpid, int orderid)
        {
            try
            {
                using (var dc = new WMSContext())
                {

                    bool boolOrdersFinished = !dc.Orders.Any(prop => prop.ERP_ID == erpid && prop.OrderID == orderid &&
                                                             prop.Status < Order.OrderStatus.Canceled);
                    if (boolOrdersFinished)
                    {
                        var orders = dc.Orders.Where(prop => prop.ERP_ID == erpid && prop.OrderID == orderid);
                        foreach (var o in orders)
                        {
                            dc.HistOrders.Add(new HistOrder
                            {
                                ID = o.ID,
                                ERP_ID = o.ERP_ID,
                                OrderID = o.OrderID,
                                SubOrderERPID = o.SubOrderERPID,
                                SubOrderID = o.SubOrderID,
                                SubOrderName = o.SubOrderName,
                                SKU_ID = o.SKU_ID,
                                SKU_Batch = o.SKU_Batch,
                                SKU_Qty = o.SKU_Qty,
                                Destination = o.Destination,
                                ReleaseTime = o.ReleaseTime,
                                Status = (HistOrder.HistOrderStatus)o.Status,
                            });

                            var cmds = dc.Commands.Where(p => p.Order_ID == o.ID);
                            foreach (var c in cmds)
                                dc.HistCommands.Add(new HistCommand
                                {
                                    ID = c.ID,
                                    Order_ID = c.Order_ID,
                                    TU_ID = c.TU_ID,
                                    Source = c.Source,
                                    Target = c.Target,
                                    Status = (HistCommand.HistCommandStatus)c.Status,
                                    Time = c.Time,
                                    LastChange = c.LastChange
                                });

                            dc.Commands.RemoveRange(cmds);
                            dc.Orders.Remove(o);
                        }
                        dc.SaveChanges();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(MoveOrderToHist));
                Debug.WriteLine(ex.Message);
            }
        }


        public bool CommandChangeNotifyERP(Command command)
        {
            try
            {
                int? erpid = null;
                int orderid = 0;

                using (var dc = new WMSContext())
                using (var ts = dc.Database.BeginTransaction())
                {
                    try
                    {
                        if (command.Order_ID != null)
                        {
                            Order order = dc.Orders.FirstOrDefault(prop => prop.ID == command.Order_ID);

                            // if single item changed to active --> put corresponding move command to active
                            if (command.Status == Command.CommandStatus.Active && order?.ERP_ID != null)
                            {
                                var cmdERP = dc.CommandERP.FirstOrDefault(p => p.ID == order.ERP_ID);
                                cmdERP.Status = CommandERP.CommandERPStatus.Active;
                                dc.SaveChanges();
                            }
                            // check if single item (one line (SKU) in order table) finished
                            bool oItemFinished = !dc.Commands
                                                  .Where(prop => prop.Status < Command.CommandStatus.Finished && prop.Order_ID == order.ID)
                                                  .Any();
                            bool oItemCanceled = dc.Commands.Any(prop => prop.Status == Command.CommandStatus.Canceled && prop.Order_ID == order.ID) &&
                                                 !dc.Commands.Any(prop => prop.Status < Command.CommandStatus.Canceled && prop.Order_ID == order.ID);

                            // check if subOrderFinished for one SKU
                            if (oItemFinished || oItemCanceled)
                            {
                                if (command.Target.StartsWith("W:32"))
                                    order.Status = oItemFinished ? Order.OrderStatus.OnTargetAll : Order.OrderStatus.OnTargetPart;
                                else
                                    order.Status = oItemFinished ? Order.OrderStatus.Finished : Order.OrderStatus.Canceled;
                                dc.SaveChanges();
                                // check if complete order finished
                                erpid = order.ERP_ID;
                                orderid = order.OrderID;
                            }

                            // One command (pallet) successfully finished
                            if (command.Status == Command.CommandStatus.Finished &&
                                (command.Target.StartsWith("W:32") || command.Target.StartsWith("T04")))
                            {
                                if (order.ERP_ID.HasValue && command.Target.StartsWith("W:32"))
                                {
                                    Xml.XmlWritePickToDocument xmlPickDocument = new Xml.XmlWritePickToDocument
                                    {
                                        DocumentID = order.SubOrderERPID,
                                        Commands = new Command[] { command }
                                    };
                                    CommandERP cmdERP;
                                    dc.CommandERP.Add(cmdERP = new CommandERP
                                    {
                                        ERP_ID = order.ERP_ID.HasValue ? order.FK_CommandERP.ERP_ID : 0,
                                        Command = xmlPickDocument.BuildXml(),
                                        Reference = xmlPickDocument.Reference(),
                                        LastChange = DateTime.Now
                                    });
                                    Log.AddLog(Log.SeverityEnum.Event, nameof(CommandChangeNotifyERP), $"CommandERP created : {cmdERP.Reference}");
                                    dc.SaveChanges();

                                    // Notify ERP
                                    Task.Run(async () => await ERP_WritePickToDocument(cmdERP, order.SubOrderID >= 1000));
                                }
                                else
                                {
                                    string doctype = order.ERP_ID.HasValue && command.Target.StartsWith("T04") ? "ATR02" : "ATR05";

                                    CommandERP cmdERP = new CommandERP
                                    {
                                        ERP_ID = order.ERP_ID.HasValue ? order.FK_CommandERP.ERP_ID : 0,
                                        Command = "<PlaceUpdate/>",
                                        Reference = "PlaceUpdate",
                                        Status = 0,
                                        Time = DateTime.Now,
                                        LastChange = DateTime.Now
                                    };
                                    dc.CommandERP.Add(cmdERP);
                                    dc.SaveChanges();
                                    Xml.XmlWriteMovementToSB xmlWriteMovement = new Xml.XmlWriteMovementToSB
                                    {
                                        DocumentID = cmdERP.ID,
                                        DocumentType = doctype,
                                        TU_IDs = new int[] { command.TU_ID }
                                    };
                                    cmdERP.Command = xmlWriteMovement.BuildXml();
                                    cmdERP.Reference = xmlWriteMovement.Reference();
                                    Log.AddLog(Log.SeverityEnum.Event, nameof(CommandChangeNotifyERP), $"CommandERP created : {cmdERP.Reference}");
                                    Task.Run(async () => await ERP_WriteMovementToSB(cmdERP, null, null, false));
                                }
                            }
                        }
                        ts.Commit();

                        MoveOrderToHist(erpid, orderid);
                        CheckForOrderCompletion(erpid, orderid);

                        return true;
                    }
                    catch (Exception)
                    {
                        ts.Rollback();
                        throw;
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

        public void UpdatePlaceNotifyERP(int TU_ID, string placeOld, string placeNew, string changeType)
        {
            try
            {
                using (var dc = new WMSContext())
                {
                    string entry = dc.Parameters.Find("InputCommand.Place").Value;

                    // check if TU is empty
                    if (placeNew == entry)
                    {
                        int noTUs = dc.TUs.Count(p => p.TU_ID == TU_ID);
                        if (noTUs > 0)
                        {
                            changeType = "ERR_TUsNotDeleted";
                            Log.AddLog(Log.SeverityEnum.Event, nameof(UpdatePlace), $"TUs not deleted on entry: {TU_ID}");
                        }
                    }

                    // notify ERP
                    string docType = null;
                    if (placeNew == entry && (changeType.StartsWith("MOVE")))
                        docType = !changeType.Contains("ERR") ? "ATR01" : "ATR03";
                    else if (changeType.StartsWith("CREATE"))
                        docType = !changeType.Contains("ERR") ? "ATR01" : "ATR03";
                    else if (changeType.StartsWith("MOVE") && placeNew == "W:out" &&
                             (placeOld == null || (placeOld != "T015" && placeOld != "T041" && placeOld != "T042" && !placeOld.StartsWith("W:32"))))
                        docType = "ATR05";      // removed
                    else if ((placeNew.StartsWith("W:32") || placeNew.StartsWith("T04") || placeNew.StartsWith("T015")) &&
                              changeType.StartsWith("MOVE") &&
                              !dc.Commands.Any(pp => pp.Target == placeNew && pp.Status == Command.CommandStatus.Active))
                        docType = "ATR05";      // non-ERP command

                    if (docType != null)
                    {
                        // first we create a command to get an ID
                        CommandERP cmd = new CommandERP
                        {
                            ERP_ID = 0,
                            Command = "<PlaceUpdate/>",
                            Reference = "PlaceUpdate",
                            Status = 0,
                            Time = DateTime.Now,
                            LastChange = DateTime.Now
                        };
                        dc.CommandERP.Add(cmd);
                        dc.SaveChanges();
                        // create xml
                        Xml.XmlWriteMovementToSB xmlWriteMovement = new Xml.XmlWriteMovementToSB
                        {
                            DocumentID = cmd.ID,
                            DocumentType = docType,
                            TU_IDs = new int[] { TU_ID }
                        };
                        cmd.Command = xmlWriteMovement.BuildXml();
                        cmd.Reference = xmlWriteMovement.Reference();
                        bool noCommand = docType == "ATR05" ||
                                         (placeNew == entry && docType == "ATR03") ||                            // dimension check automatic out
                                          dc.Commands.Any(prop => prop.Source == entry && prop.TU_ID == TU_ID && prop.Status < Command.CommandStatus.Canceled);  // second call tu Update due to InitialNotify
                        Task.Run(async () => await ERP_WriteMovementToSB(cmd, placeNew, TU_ID, !noCommand));
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        public void UpdatePlace(string placeID, int TU_ID, string changeType)
        {
            try
            {
                //                lock (this)
                {
                    string placeOld = null;
                    using (var dc = new WMSContext())
                    using (var ts = dc.Database.BeginTransaction())
                    {
                        try
                        {
                            string entry = dc.Parameters.Find("InputCommand.Place").Value;

                            Place p = dc.Places
                                        .Where(prop => prop.TU_ID == TU_ID)
                                        .FirstOrDefault();
                            placeOld = p?.PlaceID;

                            TU_ID tuid = dc.TU_IDs.Find(TU_ID);
                            if (tuid == null)
                            {
                                dc.TU_IDs.Add(new TU_ID
                                {
                                    ID = TU_ID
                                });
                                Log.AddLog(Log.SeverityEnum.Event, nameof(UpdatePlace), $"TU_IDs add : {TU_ID:d9}");
                            }
                            if (p == null || p.PlaceID != placeID)
                            {
                                if (p != null)
                                {
                                    dc.Places.Remove(p);
                                    if (placeID == entry)
                                    {
                                        IEnumerable<TU> tu = dc.TUs.Where(pp => pp.TU_ID == p.TU_ID);
                                        dc.TUs.RemoveRange(tu);
                                    }
                                }
                                dc.Places.Add(new Place
                                {
                                    PlaceID = placeID,
                                    TU_ID = TU_ID
                                });
                                Log.AddLog(Log.SeverityEnum.Event, nameof(UpdatePlace), $"{placeID},{TU_ID:d9},{changeType}");
                            }
                            dc.SaveChanges();
                            ts.Commit();
                        }
                        catch
                        {
                            ts.Rollback();
                            throw;
                        }
                        UpdatePlaceNotifyERP(TU_ID, placeOld, placeID, changeType);
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


        public void UpdateCommand(int id, int status)
        {
            try
            {
                {
                    using (var dc = new WMSContext())
                    {
                        var cmd = dc.Commands.Find(id);
                        if (cmd != null)
                        {
                            cmd.Status = (Command.CommandStatus)status;
                            cmd.LastChange = DateTime.Now;
                            dc.SaveChanges();
                            Log.AddLog(Log.SeverityEnum.Event, nameof(UpdateCommand), $"{id}, {status}");
                            // move canceled/finished command which are not part of an order to HistCommands
                            if( cmd.Order_ID == null && cmd.Status >= Command.CommandStatus.Canceled)
                            {
                                dc.HistCommands.Add(new HistCommand
                                {
                                    ID = cmd.ID,
                                    Order_ID = cmd.Order_ID,
                                    TU_ID = cmd.TU_ID,
                                    Source = cmd.Source,
                                    Target = cmd.Target,
                                    Status = (HistCommand.HistCommandStatus)cmd.Status,
                                    Time = cmd.Time,
                                    LastChange = cmd.LastChange
                                });
                                dc.Commands.Remove(cmd);
                                dc.SaveChanges();

                            }
                            CommandChangeNotifyERP(cmd);
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

        public void CheckForOrderCompletion(int? erpid, int orderid)
        {
            try
            {
                using (var dc = new WMSContext())
                {
                    bool boolOrdersFinished = !dc.Orders.Any(prop => prop.ERP_ID == erpid && prop.OrderID == orderid && 
                                                             prop.Status < Order.OrderStatus.OnTargetPart);
                    if (boolOrdersFinished && erpid.HasValue)
                    {
                        var order = dc.Orders.FirstOrDefault(prop => prop.ERP_ID == erpid.Value && prop.OrderID == orderid);

                        // set status of corresponding move command
                        var erpcmd = dc.CommandERP.FirstOrDefault(p => p.ID == order.ERP_ID);
                        erpcmd.Status = CommandERP.CommandERPStatus.Finished;
                        dc.SaveChanges();

                        // log to CommandERP
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
                            Status = CommandERP.CommandERPStatus.Finished
                        };
                        dc.CommandERP.Add(cmdERP1);
                        Log.AddLog(Log.SeverityEnum.Event, nameof(CommandChangeNotifyERP), $"CommandERP created : {cmdERP1.Reference}");
                        dc.SaveChanges();
                        // TODO-WMS call XmlReadERPCommandStatus via WCF

                        // special report to ERP
                        CommandERP cmdERP2 = new CommandERP
                        {
                            ERP_ID = erpcmd.ERP_ID,
                            Command = "<WriteResultToSB/>",
                            Reference = "WriteResultToSB",
                            Status = 0,
                            Time = DateTime.Now,
                            LastChange = DateTime.Now
                        };
                        dc.CommandERP.Add(cmdERP2);
                        dc.SaveChanges();
                        Xml.XmlWriteResultToSB xmlWriteResult = new Xml.XmlWriteResultToSB
                        {
                            ERPID = order.ERP_ID.Value,
                            OrderID = order.OrderID
                        };
                        cmdERP2.Command = xmlWriteResult.BuildXml();
                        cmdERP2.Reference = xmlWriteResult.Reference();
                        Log.AddLog(Log.SeverityEnum.Event, nameof(CommandChangeNotifyERP), $"XmlWriteResultToSB created : {cmdERP2.Reference}");
                        Task.Run(async () => await ERP_WriteResultToSB(cmdERP2));
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

        public void CancelOrderCommands(DTOOrder orderToCancel)
        {
            // orderToCancel.ID != 0 : only one suborder, 
            // orderToCancel.ID == 0 : all suborders, 
            try
            {
                using (var dc = new WMSContext())
                using (var client = new MFCS_Proxy.WMSClient())
                {
                    // get all suborders
                    var orders = dc.Orders.Where(p => (orderToCancel.ID != 0 && p.ID == orderToCancel.ID) ||
                                                      (orderToCancel.ID == 0 && p.ERP_ID == orderToCancel.ERP_ID && p.OrderID == orderToCancel.OrderID)).ToList();
                    if (orders.Count > 0)
                    {
                        foreach (var o in orders)
                        {
                            if (!dc.Commands.Any(p => p.Order_ID == o.ID))
                            {
                                o.Status = o.Destination.StartsWith("W:32") ? Order.OrderStatus.OnTargetPart : Order.OrderStatus.Canceled;
                                dc.SaveChanges();
                            }
                            else
                            {
                                var cmds = dc.Commands.Where(p => p.Order_ID == o.ID && p.Status <= Command.CommandStatus.Active);
                                foreach (var c in cmds)
                                {
                                    c.Status = Command.CommandStatus.Canceled;
                                    MFCS_Proxy.DTOCommand[] cs = new MFCS_Proxy.DTOCommand[] { c.ToProxyDTOCommand() };
                                    client.MFCS_Submit(cs);
                                }
                            }
                        }
                        CheckForOrderCompletion(orders.FirstOrDefault().ERP_ID, orders.FirstOrDefault().OrderID);
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
                                Command = "<BlockLocations />",
                                Reference = "BlockLocations",
                                Status = 0,
                                Time = DateTime.Now,
                                LastChange = DateTime.Now
                            };
                            dc.CommandERP.Add(cmd);
                            dc.SaveChanges();
                            // create xml
                            string docType = block ? "AST01" : "AST02";
                            Xml.XmlWriteMovementToSB xmlWriteMovement = new Xml.XmlWriteMovementToSB
                            {
                                DocumentID = cmd.ID,
                                DocumentType = docType,
                                TU_IDs = tuids.ToArray()
                            };
                            cmd.Command = xmlWriteMovement.BuildXml();
                            cmd.Reference = xmlWriteMovement.Reference();
                            Task.Run(async () => await ERP_WriteMovementToSB(cmd, null, null, false));
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
                            Command = "<BlockTU />",
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
                        cmd.Command = xmlWriteMovement.BuildXml();
                        cmd.Reference = xmlWriteMovement.Reference();
                        Task.Run(async () => await ERP_WriteMovementToSB(cmd, null, null, false));
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

                    bool canRelease = !dc.Places.Any(p => p.PlaceID.StartsWith(destinationtStartsWith)) &&
                                      !dc.Orders.Any(p => p.Destination.StartsWith(destinationtStartsWith) && p.Status == Order.OrderStatus.Active);

                    if (canRelease)
                    {
                        var l = from o in dc.Orders
                                where o.Destination.StartsWith(destinationtStartsWith) &&
                                      o.Status == Order.OrderStatus.OnTargetPart || o.Status == Order.OrderStatus.OnTargetAll
                                select o;
                        var oo = l.FirstOrDefault();

                        foreach (var o in l)
                        {
                            o.Status = (o.Status == Order.OrderStatus.OnTargetPart) ? Order.OrderStatus.Canceled : Order.OrderStatus.Finished;
                            var param = dc.Parameters.Find($"Counter[{o.Destination}]");
                            if (param != null)
                                param.Value = Convert.ToString(0);
                        }
                        Log.AddLog(Log.SeverityEnum.Event, nameof(ReleaseRamp), $"Ramp released: {destinationtStartsWith}");
                        dc.SaveChanges();

                        // move orders and corresponding commands to hist
                        if (oo != null)
                            MoveOrderToHist(oo.ERP_ID, oo.OrderID);
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

        public bool OrderForRampActive(string ramp)
        {
            try
            {
                using (var dc = new WMSContext())
                {
                    return dc.Orders.Any(p => p.Destination == ramp && p.Status == Order.OrderStatus.Active);
                }
            }
            catch (Exception ex)
            {
                Log.AddException(ex);
                SimpleLog.AddException(ex, nameof(Model));
                Debug.WriteLine(ex.Message);
                return false;
            }
        }

        public void SyncDatabase(List<PlaceDiff> list, string user)
        {
            try
            {
                using (var dcw = new WMSContext())
                {

                    string exit = dcw.Parameters.Find("OutOfWarehouse.Place").Value;

                    foreach (var l in list)
                    {
                        if (dcw.TU_IDs.FirstOrDefault(p => p.ID == l.TUID) == null)
                        {
                            var tuid = new TU_ID { ID = l.TUID, DimensionClass = 0, Blocked = 0 };
                            dcw.TU_IDs.Add(tuid);
                            Log.AddLog(Log.SeverityEnum.Event, $"nameof(SyncDatabase), {user}", $"Update places WMS, add TUID: {tuid.ToString()}|");
                        }
                        var place = dcw.Places.FirstOrDefault(pp => pp.TU_ID == l.TUID);
                        // delete
                        if (place != null && l.PlaceMFCS == null)
                        {
                            dcw.Places.Remove(place);
                            dcw.SaveChanges();
                            UpdatePlace(exit, l.TUID, "MOVE");
                            Log.AddLog(Log.SeverityEnum.Event, $"{nameof(SyncDatabase)}, {user}", $"Update places WMS, remove TU: {l.TUID:d9}, {place.ToString()}");
                        }
                        // move
                        else if (place != null && l.PlaceMFCS != null)
                        {
                            dcw.Places.Remove(place);
                            var pl = new Place { TU_ID = l.TUID, PlaceID = l.PlaceMFCS, Time = DateTime.Now };
                            dcw.Places.Add(pl);
                            dcw.SaveChanges();
                            UpdatePlace(l.PlaceMFCS, l.TUID, "MOVE");
                            Log.AddLog(Log.SeverityEnum.Event, $"{nameof(SyncDatabase)}, {user}", $"Update places WMS, rebook TU: {l.TUID:d9}, {place.ToString()}");
                        }
                        // create
                        else if (place == null && l.PlaceMFCS != null)
                        {
                            var pl = new Place { TU_ID = l.TUID, PlaceID = l.PlaceMFCS, Time = DateTime.Now };
                            dcw.Places.Add(pl);
                            dcw.SaveChanges();
                            UpdatePlace(l.PlaceMFCS, l.TUID, "CREATE");
                            Log.AddLog(Log.SeverityEnum.Event, $"{nameof(SyncDatabase)}, {user}", $"Update places WMS, create TU: {pl.ToString()}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                throw new Exception(string.Format("{0}.{1}: {2}", this.GetType().Name, (new StackTrace()).GetFrame(0).GetMethod().Name, e.Message));
            }
        }


    }
}