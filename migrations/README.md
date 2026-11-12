# Esquema central de RFID Lock

Ejecutar en la base de datos de destino con una cuenta de migración.

```sh
psql -X -v ON_ERROR_STOP=1 -d rfid_lock -f migrations/001_rfid_lock.sql
psql -X -v ON_ERROR_STOP=1 -d rfid_lock -f migrations/002_optional_employee_number.sql
```

- Cada máquina tiene un UUID persistente generado durante el alta, un nombre, una zona horaria IANA y una hora local de carga. El instalador arranca con protección desactivada. La aplicación debe confirmar la conexión, registrar la lectora y comprobar un escaneo antes de habilitarla. La configuración de emergencia debe funcionar localmente aunque la base central esté caída.
- Cada lectora pertenece a una máquina. Las rutas HID se guardan por dispositivo. En modo serial se guardan el puerto y la velocidad.
- Los códigos y números de reloj se guardan como texto para conservar ceros iniciales. Un gafete activo requiere un permiso vigente en la máquina. El rol del gafete define las operaciones disponibles después de validar ese permiso. La aplicación debe aplicar esta autorización, incluida la apertura de la UI administrativa.
- Cada sesión mide el tiempo entre el acceso concedido y el siguiente bloqueo, cierre o interrupción. El agente debe cerrar una sesión interrumpida con la última hora conocida antes de abrir otra en esa máquina. Las sesiones abiertas muestran una duración nula.
- El agente debe generar y conservar el UUID de cada evento en su cola local. Si usa CSV, debe conservar también la marca temporal con zona y los UUID de máquina, lectora y sesión que correspondan. El importador puede reintentar con `INSERT ... ON CONFLICT (id) DO NOTHING`. Debe sincronizar las sesiones antes de sus eventos y confirmar la transacción antes de retirar las filas de la cola.
- Limite la lectura de los códigos y los datos personales a los roles de base de datos que los necesiten. Guarde las credenciales de conexión en el equipo con protección de Windows y exclúyalas de CSV, eventos y repositorio.
