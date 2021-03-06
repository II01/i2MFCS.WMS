use [i2MFCS.WMS.Database.Tables.WMSContext]

select tab.SKUID, sum(tab.W1) 'W1', sum(tab.W2) 'W2' from 
(
	select isnull(w1.SKU_ID, w2.SKU_ID) 'SKUID', isnull(w1.Batch, w2.Batch) 'Batch', isnull(w1.cnt, 0) 'W1', isnull(w2.cnt, 0) 'W2'
	from 
	(
		select TUs.SKU_ID, TUs.Batch, count(*) cnt
		from dbo.Tus
		join dbo.Places on Places.TU_ID = TUs.TU_ID
		where Places.PlaceID like 'W:1%'
		group by TUs.SKU_ID, TUs.Batch
	) w1
	full join
	(
		select TUs.SKU_ID, TUs.Batch, count(*) cnt
		from dbo.Tus
		join dbo.Places on Places.TU_ID = TUs.TU_ID
		where Places.PlaceID like 'W:2%'
		group by TUs.SKU_ID, TUs.Batch
	) w2
	on w1.SKU_ID = w2.SKU_ID and w1.Batch = w2.Batch
) tab
--group by tab.SKUID
--order by tab.SKUID
group by tab.SKUID, tab.Batch
order by tab.SKUID, tab.Batch
