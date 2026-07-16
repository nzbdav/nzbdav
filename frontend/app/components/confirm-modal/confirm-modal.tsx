import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Checkbox } from "~/components/ui/form";
import { Modal } from "~/components/ui/modal";
import { WordWrap } from "../word-wrap/word-wrap";
import { useCallback, useState, type ReactNode } from "react";

export type ConfirmModalProps = {
    show: boolean,
    title: string,
    message: ReactNode,
    checkboxMessage?: string,
    errorMessage?: string,
    cancelText?: string,
    confirmText?: string,
    onCancel: () => void,
    onConfirm: (isCheckboxChecked?: boolean) => void,
}

export function ConfirmModal(props: ConfirmModalProps) {
    const [isCheckboxChecked, setIsCheckboxChecked] = useState(false);

    const onConfirm = useCallback((isChecked?: boolean) => {
        props.onConfirm(isChecked);
        setIsCheckboxChecked(false);
    }, [props.onConfirm, setIsCheckboxChecked]);

    const onCancel = useCallback(() => {
        props.onCancel();
        setIsCheckboxChecked(false);
    }, [props.onCancel, setIsCheckboxChecked]);

    return (
        <Modal
            open={props.show}
            onClose={onCancel}
            title={props.title}
            footer={<>
                <Button variant="outline" onClick={onCancel}>
                    {props.cancelText || "Close"}
                </Button>
                <Button variant="danger" onClick={() => onConfirm(isCheckboxChecked)}>
                    {props.confirmText || "Confirm Removal"}
                </Button>
            </>}
        >
            <div className="space-y-3 text-xs text-base-content/80">
                <WordWrap>{props.message}</WordWrap>
                {props.checkboxMessage && (
                    <label className="flex items-center gap-2 text-sm text-base-content/80">
                        <Checkbox
                            id="modal-checkbox"
                            checked={isCheckboxChecked}
                            onChange={(event) => setIsCheckboxChecked(event.target.checked)}
                        />
                        <span>{props.checkboxMessage}</span>
                    </label>
                )}
                {props.errorMessage && <Alert variant="warning">{props.errorMessage}</Alert>}
            </div>
        </Modal>
    );
}
