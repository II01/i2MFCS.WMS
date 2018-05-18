select TUs.SKU_ID, TUs.Batch, TUs.Qty, Places.TU_ID, Places.PlaceID from [Places]
join TUs on Places.TU_ID = TUs.TU_ID
where Places.PlaceID like 'W:1%' or Places.PlaceID like 'W:2%'
order by SKU_ID asc, Batch asc
