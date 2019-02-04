using i2MFCS.WMS.Core.Business;
using i2MFCS.WMS.Core.Xml.ERPCommand;
using i2MFCS.WMS.Database.Tables;
using SimpleLogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace i2MFCS.WMS.Core.Xml
{
    public class XMLParsingException : Exception
    {
        public XMLParsingException()
        {
        }

        public XMLParsingException(string message)
            : base(message)
        {
        }

        public XMLParsingException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public class XmlReadERPCommand : XmlBasicFromERP
    {

        private const string _DeffileNameSchema = @"..\..\..\i2MFCS.WMS.Core\Xml\ERPCommand.xsd";


        public XmlReadERPCommand() : base(_DeffileNameSchema)
        {
        }

        public override string Reference()
        {
            return $"{nameof(XmlReadERPCommand)}";
        }
        // Test xml->database form->xml

        // read xml->table form
        private int XmlMoveCommand(WMSContext dc, XElement move, int id)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var order in move.Elements(ns + "Order"))
                {
                    string loc = order.Element(ns + "Location").Value;
                    if (dc.PlaceIds.Find(loc) == null)
                        throw new XMLParsingException($"Destination:NOLOCATION ({loc})");
                    foreach (var suborder in order.Element(ns + "Suborders").Elements(ns + "Suborder"))
                    {
                        string name3 = suborder.Element(ns + "Name").Value;
                        string[] name = name3.Split('#');
                        if (name.Length != 5)
                            throw new XMLParsingException($"Suborder:NAMEFORMAT ({name3})");
                        foreach (var sku in suborder.Element(ns + "SKUs").Elements(ns + "SKU"))
                        {
                            string skuid = sku.Element(ns + "SKUID").Value;
                            string so = suborder.Element(ns + "SuborderID").Value;
                            if (dc.SKU_IDs.Find(skuid) == null)
                                throw new XMLParsingException($"SKUID:NOSKUID ({so}, {skuid})");
                            if (sku.Element(ns + "Batch").Value == null || sku.Element(ns + "Batch").Value.Length == 0)
                                throw new XMLParsingException($"SKUID:NOBATCH ({so}, {skuid})");
                        }
                    }
                }

                IEnumerable<Order> orders =
                    from order in move.Elements(ns + "Order")
                        from suborder in order.Element(ns + "Suborders").Elements(ns + "Suborder")
                            from sku in suborder.Element(ns + "SKUs").Elements(ns + "SKU")
                                select new Order
                                {
                                    ERP_ID = id,
                                    OrderID = XmlConvert.ToInt32(order.Element(ns + "OrderID").Value),
                                    ReleaseTime = XmlConvert.ToDateTime(order.Element(ns + "ReleaseTime").Value, XmlDateTimeSerializationMode.Local),
                                    Destination = order.Element(ns + "Location").Value,
                                    SubOrderID = XmlConvert.ToInt32(suborder.Element(ns + "SuborderID").Value),
                                    SubOrderERPID = XmlConvert.ToInt32(suborder.Element(ns + "SuborderERPID").Value),
                                    SubOrderName = suborder.Element(ns + "Name").Value,
                                    SKU_ID = sku.Element(ns + "SKUID").Value,
                                    SKU_Qty = double.Parse(sku.Element(ns + "Quantity").Value, System.Globalization.NumberStyles.Any),
                                    SKU_Batch = sku.Element(ns + "Batch").Value,
                                    Status = 0
                                };

                dc.Orders.AddRange(orders);
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        private int XmlDeleteSKUCommand(WMSContext dc, XElement deleteSku)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var tuid in deleteSku.Elements(ns + "TU" ))
                {
                    int tuidkey = XmlConvert.ToInt32(tuid.Element(ns + "TUID").Value);
                    if(dc.TUs.FirstOrDefault(p => p.TU_ID == tuidkey) == null)
                        throw new XMLParsingException($"TUID:NOTUID ({tuidkey:d9})");
                    IEnumerable<TU> tu = dc.TUs.Where(prop => prop.TU_ID == tuidkey);
                    dc.TUs.RemoveRange(tu);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        private int XmlCreateSKUCommand(WMSContext dc, XElement createSku)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var tu in createSku.Elements(ns + "TU"))
                {
                    int tukey = XmlConvert.ToInt32(tu.Element(ns + "TUID").Value);
                    if (dc.TU_IDs.Find(tukey) == null)
                        throw new XMLParsingException($"TUID:NOTUID ({tukey:d9})");
                    foreach (var sku in tu.Element(ns + "SKUs").Elements(ns + "SKU"))
                    {
                        string skukey = sku.Element(ns + "SKUID").Value;
                        if(dc.SKU_IDs.Find(skukey) == null)
                            throw new XMLParsingException($"SKUID:NOSKUID ({skukey})");
                        dc.TUs.Add(new TU
                        {
                            TU_ID = XmlConvert.ToInt32(tu.Element(ns + "TUID").Value),
                            SKU_ID = skukey,
                            Qty = double.Parse(sku.Element(ns + "Quantity").Value, System.Globalization.NumberStyles.Any),
                            Batch = sku.Element(ns + "Batch").Value,
                            ProdDate = XmlConvert.ToDateTime(sku.Element(ns + "ProdDate").Value, XmlDateTimeSerializationMode.Local),
                            ExpDate = XmlConvert.ToDateTime(sku.Element(ns + "ExpDate").Value, XmlDateTimeSerializationMode.Local)
                        });
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        private int XmlDeleteTUCommand(WMSContext dc, XElement deleteTU, string exitPlace)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var tuid in deleteTU.Elements(ns + "TU"))
                {
                    int tuidkey = XmlConvert.ToInt32(tuid.Element(ns + "TUID").Value);
                    string loc = tuid.Element(ns + "Location").Value;
                    IEnumerable<TU> tu = dc.TUs.Where(prop => prop.TU_ID == tuidkey);
                    Place place = dc.Places.Where(prop => prop.TU_ID == tuidkey).FirstOrDefault();
                    if(place == null)
                        throw new XMLParsingException("TUID:NOTUID");
                    if (place.PlaceID == loc)
                    {
                        dc.Places.Remove(place);
                        string entry = dc.Parameters.Find("InputCommand.Place").Value;
                        Place p = new Place { TU_ID = place.TU_ID, PlaceID = exitPlace };
                        dc.Places.Add(p);
                        dc.SaveChanges();
//                        dc.TUs.RemoveRange(tu);
                    }
                    else
                        throw new XMLParsingException($"Location:NOTUIDONLOCATION ({tuidkey:d9}, {loc})");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw; 
            }
        }

        private int XmlCreateTUCommand(WMSContext dc, XElement createTU)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var tu in createTU.Elements(ns + "TU"))
                {
                    int key = XmlConvert.ToInt32(tu.Element(ns + "TUID").Value);
                    string loc = tu.Element(ns + "Location").Value;

                    TU_ID tuid = dc.TU_IDs.Find(key);
                    if (tuid == null)
                    {
                        tuid = new TU_ID { ID = key };
                        dc.TU_IDs.Add(tuid);
                    }
                    else
                        throw new XMLParsingException($"TUID:TUIDEXISTS ({key:d9})");
                    if (dc.PlaceIds.Find(loc) == null)
                        throw new XMLParsingException($"Location:NOLOCATION ({loc})");
                    if (dc.Places.FirstOrDefault(prop => prop.PlaceID == loc && prop.FK_PlaceID.DimensionClass >= 0 && prop.FK_PlaceID.DimensionClass < 999) != null)
                        throw new XMLParsingException($"Location:LOCATIONFULL ({loc})");
                    try
                    {
                        tuid.Blocked = XmlConvert.ToInt32(tu.Element(ns + "Blocked").Value);
                    }
                    catch
                    {
                        tuid.Blocked = 0;
                    }
                    tuid.DimensionClass = 0;
                    Place p = dc.Places.FirstOrDefault(prop => prop.TU_ID == key);
                    if (p == null)
                    {
                        p = new Place { TU_ID = key };
                        dc.Places.Add(p);
                    }
                    p.PlaceID = loc;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        private int XmlChangeTUCommand(WMSContext dc, XElement change)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var tu in change.Elements(ns + "TU"))
                {
                    int tuidkey = XmlConvert.ToInt32(tu.Element(ns + "TUID").Value);

                    string loc = null;
                    try
                    {
                        loc = tu.Element(ns + "Location").Value;
                    }
                    catch { }
                    if (loc != null)
                    {
                        var p = dc.Places.FirstOrDefault(prop => prop.TU_ID == tuidkey);
                        if (p != null)
                            dc.Places.Remove(p);
                        else
                            throw new XMLParsingException($"TUID:NOTUID ({tuidkey:d9})");
                        if (dc.PlaceIds.Find(loc) == null)
                            throw new XMLParsingException($"Location:NOLOCATION ({loc})");
                        if (dc.Places.FirstOrDefault(prop => prop.PlaceID == loc && prop.FK_PlaceID.DimensionClass >= 0 && prop.FK_PlaceID.DimensionClass < 999) != null)
                            throw new XMLParsingException($"Location:LOCATIONFULL ({loc})");
                        dc.Places.Add(new Place
                        {
                            PlaceID = tu.Element(ns + "Location").Value,
                            TU_ID = tuidkey,
                        });
                    }
                    try
                    {
                        var tuid = dc.TU_IDs.Find(tuidkey);
                        if (XmlConvert.ToInt32(tu.Element(ns + "Blocked").Value) == 1)
                            tuid.Blocked |= 4; // quality block
                        else
                        {
                            int mask = int.MaxValue ^ 4;
                            tuid.Blocked &= mask; // quality block
                        }
                    }
                    catch { }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        private int XmlSKUIDUpdateCommand(WMSContext dc, XElement update)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                foreach (var sk in update.Elements(ns + "SKUID"))
                {
                    string key = sk.Element(ns + "ID").Value;
                    SKU_ID skuid = dc.SKU_IDs.Find(key);
                    if (skuid == null)
                    {
                        skuid = new SKU_ID { ID = key };
                        dc.SKU_IDs.Add(skuid);
                    }
                    skuid.Description = sk.Element(ns + "Description").Value;
                    skuid.DefaultQty = double.Parse(sk.Element(ns + "Quantity").Value, System.Globalization.NumberStyles.Any);
                    skuid.Unit = sk.Element(ns + "Unit").Value;
                    skuid.Weight = double.Parse(sk.Element(ns + "Weight").Value, System.Globalization.NumberStyles.Any);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        private int XmlCancelCommand(WMSContext dc, XElement cancel)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                int cancelKey = XmlConvert.ToInt32(cancel.Element(ns + "CommandID").Value);
                var cmd = dc.CommandERP.FirstOrDefault(p => p.ERP_ID == cancelKey);
                if (cmd != null && cmd.Status < CommandERP.CommandERPStatus.Canceled)
                    cmd.Status = CommandERP.CommandERPStatus.Canceled;
                else if (cmd == null)
                    throw new XMLParsingException($"CommandID:NOCOMMANDID ({cancelKey})");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }

        // todo make reply
        private int XmlStatusCommand(WMSContext dc, XElement status)
        {
            try
            {
                XNamespace ns = XDocument.Root.Name.Namespace;

                int statusKey = XmlConvert.ToInt32(status.Element(ns + "CommandID").Value);
                var cmd = dc.CommandERP.FirstOrDefault(p => p.ERP_ID == statusKey);
                if(cmd == null)
                    throw new XMLParsingException($"CommandID:NOCOMMANDID ({statusKey})");
                return 100 + (int)cmd.Status;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }


        private string XmlProces(string xml)
        {
            XElement el0 = null;
            bool fault = false;
            XDocument XOutDocument = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), el0 = new XElement("ERPsubmitStatus"));
            XNamespace nsOut = XOutDocument.Root.Name.Namespace;

            try
            {

                using (var dc = new WMSContext())
                {

                    el0.Add(new XElement("xmlcommandstring"));
                    el0.Add(new XElement("Commands"));

                    LoadXml(xml);
                    XNamespace ns = XDocument.Root.Name.Namespace;

                    foreach (var cmd in XDocument.Root.Elements())
                    {
                        XElement elC = null;
                        XDocument XOutDocumentC = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), elC = new XElement("Command"));
                        XNamespace nsOutC = XOutDocumentC.Root.Name.Namespace;

                        var cmdERP = new CommandERP
                        {
                            Command = cmd.ToString(),
                            ERP_ID = Convert.ToInt32(cmd.Element(ns + "ERPID").Value),
                            Reference = Reference() + $"(ERP_ID = {cmd.Element(ns + "ERPID").Value}, Action = {cmd.Name.LocalName})",
                            Status = cmd.Name.LocalName == "Move" ? CommandERP.CommandERPStatus.NotActive : CommandERP.CommandERPStatus.Finished,
                            LastChange = DateTime.Now
                        };
                        try
                        {
                            try
                            {
                                dc.CommandERP.Add(cmdERP);
                                dc.SaveChanges();
                            }
                            catch
                            {
                                throw new XMLParsingException($"ERPID:ERPIDEXISTS ({cmdERP.ERP_ID})");
                            }

                            int status = 0;

                            switch (cmd.Name.LocalName)
                            {
                                case "SKUIDUpdate":
                                    status = XmlSKUIDUpdateCommand(dc, cmd);
                                    break;
                                case "TUCreate":
                                    status = XmlCreateTUCommand(dc, cmd);
                                    break;
                                case "TUDelete":
                                    status = XmlDeleteTUCommand(dc, cmd, dc.Parameters.Find("OutOfWarehouse.Place").Value);
                                    break;
                                case "TUChange":
                                    status = XmlChangeTUCommand(dc, cmd);
                                    break;
                                case "TUCreateSKU":
                                    status = XmlCreateSKUCommand(dc, cmd);
                                    break;
                                case "TUDeleteSKU":
                                    status = XmlDeleteSKUCommand(dc, cmd);
                                    break;
                                case "Move":
                                    status = XmlMoveCommand(dc, cmd, cmdERP.ID);
                                    break;
                                case "Cancel":
                                    status = XmlCancelCommand(dc, cmd);
                                    break;
                                case "Status":
                                    status = XmlStatusCommand(dc, cmd);
                                    break;
                                default:
                                    throw new Exception();
                            }
                            dc.SaveChanges();
                            el0.Element(nsOut + "Commands").Add(new XElement("Command"));
                            (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("ERPID", cmdERP.ERP_ID));
                            (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("Status", 0));

                            elC.Add(new XElement("ERPID", cmdERP.ERP_ID));
                            elC.Add(new XElement("Status", 0));

                            if (status >= 100) // to immediatelly return status of command in question
                            {
                                string st;
                                switch (status%100)
                                {
                                    case 1: st = "ACTIVE"; break;
                                    case 2: st = "CANCELED"; break;
                                    case 3: st = "FINISHED"; break;
                                    default: st = "WAITING"; break;
                                }
                                (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("Details", st));
                                elC.Add(new XElement("Details", st));
                            }
                            else
                                (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("Details", ""));
                            (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("ExtraInfo", ""));

                        }
                        catch (Exception ex)
                        {
                            fault = true;
                            el0.Element(nsOut + "Commands").Add(new XElement("Command"));
                            (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("ERPID", cmdERP.ERP_ID));
                            (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("Status", 1));
                            if(ex is XMLParsingException )
                            {
                                string[] s = ex.Message.Split(':');
                                if (s.Length > 1)
                                {
                                    (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("Details", s[0]));
                                    (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("ExtraInfo", s[1]));
                                    elC.Add(new XElement("Details", s[0]));
                                    elC.Add(new XElement("ExtraInfot", s[1]));
                                }
                            }
                            else
                            {
                                (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("Details", "OTHER"));
                                (el0.Element(nsOut + "Commands").LastNode as XElement).Add(new XElement("ExtraInfo", ex.Message));
                                elC.Add(new XElement("Details", "OTHER"));
                                elC.Add(new XElement("ExtraInfot", ex.Message));
                            }
                            Log.AddException(ex);
                            Debug.WriteLine(ex.Message);
                            SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                        }
                        try
                        {
                            using (var dcc = new WMSContext())
                            {
                                var c = dcc.CommandERP.Find(cmdERP.ID);
                                if (c != null)
                                {
                                    c.Command = $"{c.Command}\n\n<!-- reply\n{elC}\n-->";
                                    dcc.SaveChanges();
                                }
                            }
                        }
                        catch (Exception)
                        {; }
                    }
                }
                el0.Element(nsOut + "xmlcommandstring").Add(new XElement("Status", fault ? 1 : 0));
                el0.Element(nsOut + "xmlcommandstring").Add(new XElement("ExtraInfo", ""));

                return XOutDocument.ToString();
            }
            catch (Exception ex)
            {
                el0.Element(nsOut + "xmlcommandstring").Add(new XElement("Status", 1));
                el0.Element(nsOut + "xmlcommandstring").Add(new XElement("ExtraInfo", ex.Message));
                Log.AddException(ex);
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                return XOutDocument.ToString();
            }
        }

        public void ProcessXml(ERPCommand.ERPCommand erpCommands)
        {
            using (var dc = new WMSContext())
            {
                foreach (MoveType move in erpCommands.Move)
                    foreach (SuborderType sorder in move.Order.Suborders)
                        foreach (SKUCallOutType sku in sorder.SKUs)
                        {
                            Order o = new Order
                            {
                                ERP_ID = move.ERPID,
                                OrderID = move.Order.OrderID,
                                ReleaseTime = move.Order.ReleaseTime,
                                Destination = move.Order.Location,
                                SubOrderID = sorder.SuborderID,
                                SubOrderERPID = sorder.SuborderERPID,
                                SubOrderName = sorder.Name,
                                SKU_ID = sku.SKUID,
                                SKU_Qty = sku.Quantity,
                                SKU_Batch = sku.Batch,
                                Status = 0
                            };
                        }
                dc.SaveChanges();
            }
        }
        public override string ProcessXml(string xml)
        {
            try
            {
                lock (Model.Singleton())
                    return XmlProces(xml);                
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                SimpleLog.AddException(ex, nameof(XmlReadERPCommand));
                throw;
            }
        }


        #region Test xml 
        public void FullTest(string xml)
        {
            LoadXml(xml);
            XNamespace ns = XDocument.Root.Name.Namespace;
            var fromXml =
                        (from move in XDocument.Root.Elements(ns + "Move")
                         from order in move.Elements(ns + "Order")
                         from suborder in order.Elements(ns + "SubOrder")
                         from sku in suborder.Elements(ns + "SKU")
                         select new
                         {
                             SKUID = sku.Element(ns + "SKUID").Value,
                             Batch = sku.Element(ns + "Batch").Value,
                             Quantity = sku.Element(ns + "Quantity").Value,
                             ProdDate = sku.Element(ns + "ProdDate").Value,
                             ExpDate = sku.Element(ns + "ExpDate").Value,
                             SuborderID = suborder.Element(ns + "SubOrderID").Value,
                             SuborderERPID = suborder.Element(ns + "SubOrderERPID").Value,
                             SuborderName = suborder.Element(ns + "Name").Value,
                             OrderID = order.Element(ns + "OrderID").Value,
                             OrderLocation = order.Element(ns + "Location").Value,
                             OrderReleaseTime = order.Element(ns + "ReleaseTime").Value,
                             ERPID = move.Element(ns + "ERPID").Value
                         }).ToList();

            // prepare move commands back to xml
            // more for testing purposes
            var toXml = (
                         from move in fromXml
                         group move by move.ERPID into moveG
                         select new
                         {
                             ERPID = moveG.Key,
                             Orders = from order in moveG
                                      group order by order.OrderID into orderG
                                      select new
                                      {
                                          OrderID = orderG.Key,
                                          orderG.First().OrderLocation,
                                          orderG.First().OrderReleaseTime,
                                          SubOrders = from order in orderG
                                                      group order by order.SuborderID into suborderG
                                                      select new
                                                      {
                                                          SuborderID = suborderG.Key,
                                                          SuborderERPID = suborderG.First().SuborderERPID,
                                                          suborderG.First().SuborderName,
                                                          SKUs = suborderG
                                                      }
                                      }
                         });

            XElement el0 = null;
            XDocument xmlOut = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                                             el0 = new XElement("ERPCommand"));

            foreach (var move in toXml)
            {
                el0.Add(new XElement("Move"));
                XElement el1 = el0.LastNode as XElement;
                el1.Add(new XElement("ERPID", move.ERPID));
                foreach (var order in move.Orders)
                {
                    el1.Add(new XElement("Order"));
                    XElement el2 = el1.LastNode as XElement;
                    el2.Add(new XElement("OrderID", order.OrderID));
                    el2.Add(new XElement("ReleaseTime", order.OrderReleaseTime));
                    el2.Add(new XElement("Location", order.OrderLocation));
                    foreach (var subOrder in order.SubOrders)
                    {
                        el2.Add(new XElement("SubOrder"));
                        XElement el3 = el2.LastNode as XElement;
                        el3.Add(new XElement("SubOrderID", subOrder.SuborderID));
                        el3.Add(new XElement("SubOrderERPID", subOrder.SuborderERPID));
                        el3.Add(new XElement("Name", subOrder.SuborderName));
                        foreach (var sku in subOrder.SKUs)
                        {
                            el3.Add(new XElement("SKU"));
                            XElement el4 = el3.LastNode as XElement;
                            el4.Add(new XElement("SKUID", sku.SKUID));
                            el4.Add(new XElement("Quantity", sku.Quantity));
                            el4.Add(new XElement("Batch", sku.Batch));
                            el4.Add(new XElement("ProdDate", sku.ProdDate));
                            el4.Add(new XElement("ExpDate", sku.ExpDate));
                        }
                    }
                }
            }

            string xmlstr = xmlOut.ToString();
            File.WriteAllText("test.xml", xmlstr);
        }

        #endregion

    }

}
