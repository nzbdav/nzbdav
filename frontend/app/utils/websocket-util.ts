export function receiveMessage(
    onMessage: (topic: string, message: string) => void
): (event: MessageEvent) => void {
    return (event) => {
        const parsed = JSON.parse(event.data);
        onMessage(parsed.Topic, parsed.Message);
    }
}