SET TERM ^ ;

CREATE PROCEDURE CARDINDEV_TS4 (
    IDDB INTEGER,
    name VARCHAR(50))
RETURNS (
    ID_DEV INTEGER,
    ID_CTRL INTEGER,
    ID_READER INTEGER,
    ID_CARD VARCHAR(32),
    ID_PEP INTEGER,
    DEVIDX INTEGER,
    OPERATION INTEGER,
    TIMEZONES INTEGER,
    STATUS INTEGER,
    ID_CARDINDEV INTEGER,
    ATTEMPTS INTEGER)
AS
begin
for select c.id_card, c.devidx, c.id_dev, c.operation, d.id_ctrl, d.id_reader, c.id_cardindev, c.ATTEMPTS, c.id_pep
     from CardInDev c
     join Device d  on (c.id_dev=d.id_dev) and (c.id_db=d.id_db)
      /*26.10.2017 Добавлена проверка на предмет того, что контроллер имеет тип 1 и 4 (работает с RFID и отпечатком пальца) и работает с идентификаторами вида 1 и 2 (RFID и отпечаток).
   Карты других типов АСервер не увидит */
    --left join card cc on cc.id_card=c.id_card
    join device d2 on d2.id_ctrl=d.id_ctrl and (d2.id_devtype in (1,2, 4, 6)) and d2.id_reader is null
    where (c.id_db=:iddb) and ( 0 <> (select IS_ACTIVE from DEVICE_CHECKACTIVE(d.id_dev)) )
    and d2.name=:name
    order by c.id_cardindev
    into :id_card, :devidx, :id_dev, :operation, :id_ctrl, :id_reader, :id_cardindev, :attempts, :id_pep

/* Update 10.01.2015*/

do begin
    if (operation=1) then
      begin
        select c_gp.timezones, c_gp.status
        from card_getparam4dev_mul(:iddb, :id_dev, :id_pep) c_gp
        /*where (c_gp.id_dev=:id_dev)*/
        into :timezones, :status;

      end
   suspend;
   end
end
^

SET TERM ; ^

DESCRIBE PROCEDURE CARDINDEV_TS4
'30.11.2024 Получение списка карточек для активных устройств, с которыми не были произведены нужные действия (удаление/запись в/из контроллеры)
Процедура создана для работы под управлением TS3';

DESCRIBE PARAMETER NAME PROCEDURE CARDINDEV_TS4
'id_dev контроллера';

GRANT SELECT ON CARDINDEV TO PROCEDURE CARDINDEV_TS4;

GRANT SELECT ON DEVICE TO PROCEDURE CARDINDEV_TS4;

GRANT EXECUTE ON PROCEDURE DEVICE_CHECKACTIVE TO PROCEDURE CARDINDEV_TS4;

GRANT EXECUTE ON PROCEDURE CARD_GETPARAM4DEV_MUL TO PROCEDURE CARDINDEV_TS4;

GRANT EXECUTE ON PROCEDURE CARDINDEV_TS4 TO SYSDBA;