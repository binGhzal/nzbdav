import { useEffect, useState } from "react";
import styles from "./live-usenet-connections.module.css";
import { createReconnectingWebSocket } from "~/utils/websocket-util";
import { useNavigate } from "react-router";
import { getWebsocketUrl } from "~/utils/url-base";

const usenetConnectionsTopic = {'cxs': 'state'};

export function LiveUsenetConnections() {
    const navigate = useNavigate();
    const [connections, setConnections] = useState<string | null>(null);
    const parts = (connections || "0|0|0|0|1|0").split("|");
    const [_0, _1, _2, live, max, idle] = parts.map(x => Number(x));
    const active = live - idle;
    const activePercent = 100 * (active / max);
    const livePercent = 100 * (live / max);

    useEffect(() => {
        return createReconnectingWebSocket({
            createSocket: () => new WebSocket(getWebsocketUrl()),
            onMessage: (_, message) => setConnections(message),
            onOpen: socket => socket.send(JSON.stringify(usenetConnectionsTopic)),
            onClose: event => {
                if (event.code == 1008) navigate('/login');
                setConnections(null);
            },
        });
    }, [navigate, setConnections]);

    return (
        <div className={styles.container}>
            <div className={styles.title}>
                Usenet Connections
            </div>
            <div className={styles.bar}>
                <div className={styles.max} />
                <div className={styles.live} style={{ width: `${livePercent}%` }} />
                <div className={styles.active} style={{ width: `${activePercent}%` }} />
            </div>
            <div className={styles.caption}>
                {connections && `${live} connected / ${max} max`}
                {!connections && `Loading...`}
            </div>
            {connections &&
                <div className={styles.caption}>
                    ( {active} active )
                </div>
            }
        </div>
    );
}
