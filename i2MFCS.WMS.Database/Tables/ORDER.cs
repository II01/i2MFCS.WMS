﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace i2MFCS.WMS.Database.Tables
{
    public class Order
    {
        [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }
        [Required, ForeignKey("FK_CommandERP")]
        public int ERP_ID { get; set; }
        [Required]
        public int OrderID { get; set; }
        [Required]
        public int SubOrderID { get; set; }
        [Required, ForeignKey("FK_SKU_ID")]
        public string SKU_ID { get; set; }
        [Required]
        public string SubOrderName { get; set; }
        [Required]
        public double SKU_Qty { get; set; }
        [Required, ForeignKey("FK_Destination")]
        public string Destination { get; set; }
        [Required]
        public DateTime ReleaseTime { get; set; }
        [Required]
        public string SKU_Batch { get; set; }
        [Required]
        public int Status { get; set; }

        public virtual CommandERP FK_CommandERP { get; set; }
        public virtual SKU_ID FK_SKU_ID { get; set; }
        public virtual PlaceID FK_Destination { get; set; }
        [InverseProperty("FK_Order_ID")]
        public virtual List<Command> FK_Command { get; set; }
        public override string ToString()
        {
            return $"({ID},{ERP_ID},{SKU_Batch},{Status}) {SKU_ID}x{SKU_Qty}->{Destination}";
        }

    }
}
