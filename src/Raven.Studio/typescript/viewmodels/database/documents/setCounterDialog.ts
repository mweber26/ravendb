import dialogViewModelBase = require("viewmodels/dialogViewModelBase");
import database = require("models/resources/database")
import setCounterCommand = require("commands/database/documents/counters/setCounterCommand");
import eventsCollector = require("common/eventsCollector");

class setCounterDialog extends dialogViewModelBase {
   
    result = $.Deferred<void>();

    createNewCounter = ko.observable<boolean>();
    counterName = ko.observable<string>();
    
    totalValue = ko.observable<number>();
    newTotalValue = ko.observable<number>();
    
    counterValuesPerNode = ko.observableArray<nodeCounterValue>();

    spinners = {
        update: ko.observable<boolean>(false)
    };    

    validationGroup = ko.validatedObservable({
        counterName: this.counterName,
        newTotalValue: this.newTotalValue
    });

    constructor(counter: counterItem, private documentId: string,  private db: database) {
        super();
        
        this.createNewCounter(!counter.counterName);
        this.counterName(counter.counterName);
        
        const currentValue = this.createNewCounter() ? 0: counter.totalCounterValue;
        this.totalValue(currentValue); 
        
        this.counterValuesPerNode(counter.counterValuesPerNode);
     
        this.initValidation();
    }

    updateCounter() {
        if (this.isValid(this.validationGroup)) {
            eventsCollector.default.reportEvent("counter", "update");

            this.spinners.update(true);

            const counterDeltaValue = this.newTotalValue() - this.totalValue();

            new setCounterCommand(this.counterName(), counterDeltaValue, this.documentId, this.db)
                .execute()
                .done(() => this.result.resolve())
                .always(() => {
                    this.spinners.update(false);
                    this.close();
                })
        }
    }
    
    private initValidation() {
        this.counterName.extend({
           required: true
        });
        
        this.newTotalValue.extend({
            required: true, 
            number: true
        });
    }
}

export = setCounterDialog;